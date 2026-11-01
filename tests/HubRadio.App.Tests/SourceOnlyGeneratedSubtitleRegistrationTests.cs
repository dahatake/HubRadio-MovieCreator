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
using HubRadio.Core.Analysis;
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
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using Microsoft.Data.Sqlite;
using ProjectSnapshotJson = HubRadio.App.Policy.ProjectSnapshotJson;

namespace HubRadio.App.Tests;

/// <summary>
/// Real planner, subtitle builder, registration and stage-checkpoint codecs on explicitly
/// synthetic machine timing. No native process, model, rendering, QC, permission or approval.
/// Kept separate from the resource/lifetime owner's SourceOnlyApplicationRuntimeTests.
/// </summary>
[TestClass]
public sealed class SourceOnlyGeneratedSubtitleRegistrationTests
{
    private const string Actor = "S-1-5-21-111-222-333-1001";
    private static readonly DateTimeOffset At = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    // Hang-probe: the real ASR shape (18 English words in ~8 s, word times, leading-space tokens) must compose.
    [TestMethod]
    public async Task RealisticEnglishSpeechTranscriptBuildsRegistersAndCheckpointsItsGeneratedSubtitles()
    {
        await using var fixture = new Fixture(0, realisticEnglish: true);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.RegisterGeneratedAsync(registrations);
        Assert.IsNotEmpty(generated.LandscapeSubtitles.Subtitles.Revisions);
    }

    [TestMethod]
    [DataRow(0, 1920, 1080)]
    [DataRow(3, 1080, 1920)]
    [DataRow(3, 1920, 1080)]
    public async Task GeneratedCallbackRegistersActualLandscapeAndEveryShortHistory(int shortCount, int sourceWidth, int sourceHeight)
    {
        await using var fixture = new Fixture(shortCount, sourceWidth, sourceHeight);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        await fixture.RegisterThroughCompositionAsync(registrations);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, registrations.Execute(
            Read(fixture.Project.Id, fixture.Preparation.Child), Actor, CancellationToken.None).Status);

        SourceOnlyGeneratedRuntimeEvidence generated = fixture.BuildGenerated();
        await registrations.Callbacks.GeneratedReady!(generated, CancellationToken.None);

        Assert.HasCount(shortCount + 1, generated.Versions);
        Assert.HasCount(shortCount, generated.ShortForms);
        Assert.AreSame(generated.Preparation.Child, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id).CompositionVersion);
        var seenCueIds = new HashSet<EntityId<CueIdTag>>();
        foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in generated.Versions.Values)
        {
            BridgeRequest request = Read(fixture.Project.Id, item.Version);
            LayoutSubtitleBridgeSnapshot snapshot = Available(registrations.Execute(request, Actor, CancellationToken.None));
            Assert.AreSame(item.Version, snapshot.CompositionVersion);
            Assert.AreEqual(item.Profile.Width, snapshot.OutputPixelWidth);
            Assert.AreEqual(item.Profile.Height, snapshot.OutputPixelHeight);
            Assert.IsNotEmpty(item.Histories, "The fixture must exercise real generated cues, not an empty registration.");
            Assert.HasCount(item.Histories.Count, snapshot.Subtitles);
            Assert.AreSame(item.Build.Subtitles.DisplayProfile, snapshot.SubtitleDisplays.Single().Profile);
            foreach (SubtitleCueHistory history in item.Histories)
            {
                LayoutSubtitleCueBinding binding = snapshot.Subtitles.Single(cue => cue.History.CueId == history.CueId);
                Assert.AreSame(history, binding.History);
                Assert.AreSame(item.Layout, binding.LayoutSpec);
                Assert.AreEqual(item.Version.Id, history.CurrentRevision.CompositionVersionId);
                Assert.AreEqual(item.Version.ContentDigest, history.CurrentRevision.CompositionVersionDigest);
                Assert.AreEqual(generated.CreatedAt, history.CurrentRevision.CreatedAt);
                Assert.AreEqual(item.Build.Subtitles.DisplayProfile.VersionDigest, binding.LayoutProfile.VersionDigest);
                CollectionAssert.AreEqual(item.Layout.SafeZones.Select(zone => zone.Name).ToArray(), binding.LayoutProfile.RequiredSafeZoneNames.ToArray());
                Assert.IsTrue(seenCueIds.Add(history.CueId), "No cue from one output version may replace another's cue.");
            }
            using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(request,
                LayoutSubtitleBridgeOperationResult.Available(snapshot)));
            JsonElement result = response.RootElement.GetProperty("result");
            Assert.AreEqual("available", result.GetProperty("status").GetString());
            JsonElement json = result.GetProperty("snapshot");
            Assert.AreEqual(item.Version.Id.ToString(), json.GetProperty("compositionVersionId").GetString());
            Assert.AreEqual(item.Version.ContentDigest.Hex, json.GetProperty("compositionVersionDigest").GetString());
            Assert.AreEqual(item.Layout.Id.ToString(), json.GetProperty("layout").GetProperty("layoutSpecId").GetString());
            Assert.AreEqual("notPersisted", json.GetProperty("persistence").GetString());
            Assert.AreEqual(JsonValueKind.Null, json.GetProperty("variantPair").ValueKind);
            Assert.AreEqual(item.Histories.Count, json.GetProperty("subtitles").GetArrayLength());
            if (item.Version.Id == generated.Preparation.Child.Id || sourceWidth > sourceHeight)
            {
                Assert.IsTrue(snapshot.SubtitleOnly);
                Assert.IsFalse(snapshot.SupportsManualLayout);
                Assert.IsNull(snapshot.ManualSelection);
                Assert.IsTrue(json.GetProperty("subtitleOnly").GetBoolean());
                Assert.IsTrue(json.GetProperty("layout").GetProperty("regions").EnumerateArray()
                    .All(region => region.GetProperty("role").GetString() is "subtitle" or "graphic"));
            }
            else
            {
                Assert.IsFalse(snapshot.SubtitleOnly);
                Assert.IsNotNull(snapshot.ManualSelection);
                Assert.IsTrue(snapshot.SupportsManualLayout);
            }
            Assert.AreEqual(sourceWidth, snapshot.Sources.Single().DisplayGeometry.Width);
            Assert.AreEqual(sourceHeight, snapshot.Sources.Single().DisplayGeometry.Height);
            Assert.IsFalse(response.RootElement.GetRawText().Contains(Actor, StringComparison.Ordinal));
        }
        Assert.AreEqual(TranscriptChangeSource.Machine, fixture.Import.Revision.ChangeSource);
        Assert.AreEqual(RightsStatus.Unconfirmed, fixture.Project.Assets.Single().Rights.Status);
        Assert.AreEqual(0, fixture.WorkCalls);
    }

    [TestMethod]
    public async Task PinnedReadsNeverSwapVersionsOrChangeTheDefaultAndUnpinnedMultiVersionApplyConflicts()
    {
        await using var fixture = new Fixture(3);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.RegisterGeneratedAsync(registrations);
        LayoutSubtitleBridgeSnapshot primary = registrations.LayoutSubtitles.GetRequired(fixture.Project.Id);
        foreach (var item in generated.Versions.Values.Reverse())
        {
            Assert.AreEqual(item.Version.Id, Available(registrations.Execute(Read(fixture.Project.Id, item.Version), Actor,
                CancellationToken.None)).CompositionVersion.Id);
            Assert.AreSame(primary, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id));
        }
        SourceOnlyGeneratedSubtitleRuntimeEvidence shortVersion = generated.Versions.Values.First(item => item.Version.Id != primary.CompositionVersion.Id);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, registrations.Execute(
            Read(fixture.Project.Id, shortVersion.Version, ContentDigest.Compute([255])), Actor, CancellationToken.None).Status);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, registrations.Execute(
            Read(EntityId<ProjectIdTag>.New(), shortVersion.Version), Actor, CancellationToken.None).Status);
        var missing = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), fixture.Project.Id, null, Actor, At,
            "Unknown version fixture", shortVersion.Version.ContentDigest, shortVersion.Version.Elements);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, registrations.Execute(Read(fixture.Project.Id, missing), Actor, CancellationToken.None).Status);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, registrations.Execute(
            Apply(primary, TextEdit(primary.Subtitles[0].History, "未指定版には適用しない。"), pinned: false), Actor, CancellationToken.None).Status);
        Assert.AreSame(primary, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id));
    }

    [TestMethod]
    public async Task ShortEditsUseTheirOwnBranchAfterOtherVersionReadsAndReplayKeepsTheSameChild()
    {
        await using var fixture = new Fixture(3);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.RegisterGeneratedAsync(registrations);
        var shorts = generated.Versions.Values.Where(item => item.Version.Id != fixture.Preparation.Child.Id).ToArray();
        LayoutSubtitleBridgeSnapshot first = Available(registrations.Execute(Read(fixture.Project.Id, shorts[0].Version), Actor, CancellationToken.None));
        LayoutSubtitleBridgeSnapshot second = Available(registrations.Execute(Read(fixture.Project.Id, shorts[1].Version), Actor, CancellationToken.None));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Invalid, registrations.Execute(
            Apply(second, TextEdit(first.Subtitles[0].History, "他の版への誤適用。")), Actor, CancellationToken.None).Status);
        Assert.AreSame(second, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id, shorts[1].Version.Id));

        LayoutSubtitleBridgeSnapshot child = Available(registrations.Execute(
            Apply(first, TextEdit(first.Subtitles[0].History, "縦型の字幕を修正。")), Actor, CancellationToken.None));
        Assert.AreEqual(first.CompositionVersion.Id, child.CompositionVersion.ParentVersionId);
        Assert.HasCount(2, child.Subtitles[0].History.Revisions);
        Assert.AreSame(second, registrations.LayoutSubtitles.GetLatest(fixture.Project.Id, second.CompositionVersion.Id));
        Assert.AreSame(first, Available(registrations.Execute(Read(fixture.Project.Id, first.CompositionVersion), Actor, CancellationToken.None)));
        Assert.AreSame(child, Available(registrations.Execute(Read(fixture.Project.Id, child.CompositionVersion), Actor, CancellationToken.None)));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, registrations.Execute(
            Apply(first, TextEdit(first.Subtitles[0].History, "旧版から別の子を作らない。")), Actor, CancellationToken.None).Status);

        // Rebuilding these bounded deterministic inputs is the production checkpoint
        // decoder's replay, not a fresh ASR/generation run or recreated editor revision.
        SourceOnlyGeneratedRuntimeEvidence replay = fixture.BuildGenerated();
        await fixture.RegisterThroughCompositionAsync(registrations);
        await registrations.Callbacks.GeneratedReady!(replay, CancellationToken.None);
        await registrations.Callbacks.GeneratedReady(replay, CancellationToken.None);
        Assert.AreSame(child, registrations.LayoutSubtitles.GetLatest(fixture.Project.Id, first.CompositionVersion.Id));
        Assert.AreSame(child, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id, child.CompositionVersion.Id));
        Assert.AreEqual("縦型の字幕を修正。", child.Subtitles[0].History.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(fixture.Preparation.Child.Id, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id).CompositionVersion.Id);
        Assert.IsNull(fixture.Services.Projects.GetCompositionVersion(child.CompositionVersion.Id), "This legacy editor must not claim persistence.");
        Assert.AreEqual(0, fixture.WorkCalls);
    }

    [TestMethod]
    public async Task LandscapeTextAndTimingRevisionsSurviveIdenticalGenerationReplayAndRemainExplicitlyUnpersisted()
    {
        await using var fixture = new Fixture(0);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        await fixture.RegisterGeneratedAsync(registrations);
        LayoutSubtitleBridgeSnapshot original = registrations.LayoutSubtitles.GetRequired(fixture.Project.Id);
        LayoutSubtitleBridgeSnapshot text = Available(registrations.Execute(
            Apply(original, TextEdit(original.Subtitles[0].History, "横長の字幕を修正。")), Actor, CancellationToken.None));
        SubtitleCueHistory history = text.Subtitles[0].History;
        LayoutSubtitleBridgeSnapshot timing = Available(registrations.Execute(Apply(text, new
        {
            kind = "editSubtitle",
            cueId = history.CueId.ToString(),
            expectedRevisionId = history.CurrentRevision.Id.ToString(),
            expectedRevisionDigest = history.CurrentRevision.ContentDigest.Hex,
            edit = new { kind = "timing", outputRange = new { start = new { value = "3", rate = "1" }, duration = new { value = "2", rate = "1" } } },
            reason = "Explicit subtitle timing correction in the test editor.",
        }), Actor, CancellationToken.None));
        await registrations.Callbacks.GeneratedReady!(fixture.BuildGenerated(), CancellationToken.None);
        Assert.AreSame(timing, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id));
        Assert.HasCount(3, timing.Subtitles[0].History.Revisions);
        Assert.AreSame(original.Subtitles[0].History.CurrentRevision, timing.Subtitles[0].History.Revisions[0]);
        Assert.AreEqual(SubtitleCueChangeKind.Text, timing.Subtitles[0].History.Revisions[1].Change.Kind);
        Assert.AreEqual(SubtitleCueChangeKind.Timing, timing.Subtitles[0].History.Revisions[2].Change.Kind);
        Assert.AreEqual(text.CompositionVersion.Id, timing.CompositionVersion.ParentVersionId);
        Assert.IsNull(timing.ManualSelection);
        Assert.AreEqual(16, timing.OutputEnvelope.OutputAspectWidth);
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(Read(fixture.Project.Id, timing.CompositionVersion),
            LayoutSubtitleBridgeOperationResult.Available(timing)));
        Assert.AreEqual("notPersisted", response.RootElement.GetProperty("result").GetProperty("snapshot").GetProperty("persistence").GetString());
        Assert.IsNull(fixture.Services.Projects.GetCompositionVersion(timing.CompositionVersion.Id));
        Assert.AreEqual(TranscriptChangeSource.Machine, fixture.Import.Revision.ChangeSource);
    }

    [TestMethod]
    public async Task SubtitleOnlyVersionsRejectVideoLayoutAndKeyframeMutationsWithoutLosingTheirCues()
    {
        await using var fixture = new Fixture(3);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.RegisterGeneratedAsync(registrations);
        foreach (var item in generated.Versions.Values)
        {
            LayoutSubtitleBridgeSnapshot snapshot = registrations.LayoutSubtitles.GetRequired(fixture.Project.Id, item.Version.Id);
            Assert.IsTrue(snapshot.SubtitleOnly);
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Unavailable, registrations.Execute(
                Apply(snapshot, FullFrameManualOperation(snapshot)), Actor, CancellationToken.None).Status);
            object keyframes = new
            {
                kind = "setKeyframes",
                sourceAssetId = snapshot.Sources[0].SourceAssetId.ToString(),
                destinationSlot = "singleSubject",
                segments = new[] { new
                {
                    range = new { start = new { value = "0", rate = "1" }, duration = new { value = "1", rate = "1" } },
                    keyframes = new[] { new { time = new { value = "0", rate = "1" }, sourceCrop = FullRectDto(), destination = FullRectDto(), interpolation = "hold" } },
                } },
                evaluationTime = new { value = "0", rate = "1" },
            };
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Unavailable, registrations.Execute(Apply(snapshot, keyframes), Actor, CancellationToken.None).Status);
            Assert.AreSame(snapshot, registrations.LayoutSubtitles.GetLatest(fixture.Project.Id, item.Version.Id));
            Assert.IsNotEmpty(snapshot.Subtitles);
        }
    }

    [TestMethod]
    public async Task SameVersionManualDraftIsNotResetByGeneratedCheckpointReplay()
    {
        await using var fixture = new Fixture(3, 1080, 1920);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.RegisterGeneratedAsync(registrations);
        var version = generated.Versions.Values.First(item => item.Version.Id != fixture.Preparation.Child.Id).Version;
        LayoutSubtitleBridgeSnapshot original = registrations.LayoutSubtitles.GetRequired(fixture.Project.Id, version.Id);
        LayoutSubtitleBridgeSnapshot edited = Available(registrations.Execute(Apply(original, FullFrameManualOperation(original)), Actor, CancellationToken.None));
        Assert.AreEqual(version.Id, edited.CompositionVersion.Id);
        Assert.AreEqual(1, edited.Revision);
        Assert.AreEqual(ManualLayoutMode.SingleSubjectCrop, edited.ManualSelection!.Resolution.Mode);
        await registrations.Callbacks.GeneratedReady!(fixture.BuildGenerated(), CancellationToken.None);
        Assert.AreSame(edited, Available(registrations.Execute(Read(fixture.Project.Id, version), Actor, CancellationToken.None)));
    }

    [TestMethod]
    [DataRow("missingVersion", false)]
    [DataRow("swappedVersion", false)]
    [DataRow("otherVersionHistory", false)]
    [DataRow("missingHistory", false)]
    [DataRow("wrongTimeline", false)]
    [DataRow("wrongProfile", false)]
    [DataRow("otherVersionHistory", true)]
    public async Task InconsistentGeneratedSetIsRejectedBeforeAnyLayoutIsInstalled(string corruption, bool persistent)
    {
        await using var fixture = new Fixture(3);
        SourceOnlyApplicationRegistrations registrations = persistent ? fixture.Services.Registrations : fixture.LegacyRegistrations;
        await fixture.RegisterThroughCompositionAsync(registrations);
        SourceOnlyGeneratedRuntimeEvidence generated = fixture.BuildGenerated();
        if (persistent)
        {
            // The pipeline has no callbacks here: save the actual roots before
            // testing the host's validate-entire-set-before-registration boundary.
            await fixture.Pipeline.CheckpointGeneratedRuntimeAsync(fixture.Execution, generated, CancellationToken.None);
        }
        var versions = generated.Versions.ToDictionary(pair => pair.Key, pair => pair.Value);
        var shorts = versions.Values.Where(item => item.Version.Id != fixture.Preparation.Child.Id).ToArray();
        EntityId<CompositionVersionIdTag> target = shorts[1].Version.Id;
        switch (corruption)
        {
            case "missingVersion": versions.Remove(target); break;
            case "swappedVersion": versions[target] = shorts[0]; break;
            case "otherVersionHistory": versions[target] = shorts[1] with { Histories = shorts[0].Histories }; break;
            case "missingHistory": versions[target] = shorts[1] with { Histories = [] }; break;
            case "wrongTimeline": versions[target] = shorts[1] with { Timeline = shorts[0].Timeline }; break;
            case "wrongProfile": versions[target] = shorts[1] with { Profile = versions[fixture.Preparation.Child.Id].Profile }; break;
            default: throw new AssertFailedException("Unknown corruption fixture.");
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => registrations.Callbacks.GeneratedReady!(generated with { Versions = versions }, CancellationToken.None));
        foreach (var item in generated.Versions.Values)
        {
            Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, registrations.Execute(Read(fixture.Project.Id, item.Version), Actor, CancellationToken.None).Status);
        }
        await registrations.Callbacks.GeneratedReady!(generated, CancellationToken.None);
        Assert.AreEqual(fixture.Preparation.Child.Id, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id).CompositionVersion.Id);
    }

    [TestMethod]
    public async Task DifferentGenerationEvidenceCannotReuseARegisteredVersionIdentity()
    {
        await using var fixture = new Fixture(0);
        SourceOnlyApplicationRegistrations registrations = fixture.LegacyRegistrations;
        await fixture.RegisterGeneratedAsync(registrations);
        LayoutSubtitleBridgeSnapshot original = registrations.LayoutSubtitles.GetRequired(fixture.Project.Id);
        SourceOnlyGeneratedRuntimeEvidence changed = fixture.BuildGenerated(At.AddSeconds(4));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => registrations.Callbacks.GeneratedReady!(changed, CancellationToken.None));
        Assert.AreSame(original, registrations.LayoutSubtitles.GetRequired(fixture.Project.Id));
    }

    [TestMethod]
    public async Task EmptyMachineInputKeepsARealEmptyLandscapeWorkspaceAndDoesNotInventShorts()
    {
        await using var fixture = new Fixture(0, empty: true);
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.RegisterGeneratedAsync(fixture.LegacyRegistrations);
        LayoutSubtitleBridgeSnapshot snapshot = fixture.LegacyRegistrations.LayoutSubtitles.GetRequired(fixture.Project.Id);
        Assert.HasCount(1, generated.Versions);
        Assert.IsEmpty(generated.ShortForms);
        Assert.IsEmpty(snapshot.Subtitles);
        Assert.IsEmpty(snapshot.Terops);
        Assert.IsNull(snapshot.ManualSelection);
        Assert.IsTrue(snapshot.SubtitleOnly);
        Assert.AreEqual(fixture.Preparation.Child.Id, snapshot.CompositionVersion.Id);
        Assert.IsTrue(generated.LandscapeSubtitles.Warnings.Any(warning => warning.Reason == "no-real-asr-cues"));
    }

    [TestMethod]
    [DataRow(0, 1920, 1080)]
    [DataRow(3, 1080, 1920)]
    public async Task ProductionEditCommitsAndRestartRestoresTheSameChildWithoutMixingSessionDrafts(
        int shortCount, int sourceWidth, int sourceHeight)
    {
        await using var fixture = new Fixture(shortCount, sourceWidth, sourceHeight);
        SourceOnlyApplicationRegistrations registrations = fixture.Services.Registrations;
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.CheckpointGeneratedAsync(registrations);
        // A selected short is its OWN journal root, even though it is a child of
        // the overall composition. Never validate/capture only the landscape root.
        CompositionVersion root = shortCount == 0 ? generated.Preparation.Child
            : generated.Versions.Values.Last(item => item.Version.Id != generated.Preparation.Child.Id).Version;
        EntityId<CompositionVersionIdTag>[] roots = generated.Versions.Keys.ToArray();
        IReadOnlyList<SubtitleEditCapture> before = registrations.CaptureSubtitles(fixture.Project.Id, roots);
        Assert.HasCount(shortCount + 1, before);
        Assert.IsTrue(before.All(capture => capture.Sequence == 0));
        WorkingCopyState workingCopy = fixture.Versions.GetCurrentState(fixture.Project.Id)!;
        ContentDigest executionDigest = fixture.Execution.StateDigest;
        int jobRevision = fixture.Services.Jobs.GetJobAggregate(fixture.Execution.Request.RootJobId)!.Revision;

        LayoutSubtitleBridgeSnapshot session = registrations.LayoutSubtitles.GetRequired(fixture.Project.Id, root.Id);
        Assert.AreEqual(shortCount > 0, session.SupportsManualLayout);
        if (session.SupportsManualLayout)
        {
            session = Available(registrations.LayoutSubtitles.Execute(Apply(session, FullFrameManualOperation(session)), Actor, CancellationToken.None));
            Assert.AreEqual(1, session.Revision);
            Assert.AreEqual(ManualLayoutMode.SingleSubjectCrop, session.ManualSelection!.Resolution.Mode);
        }
        LayoutSubtitleBridgeSnapshot initial = Available(registrations.Execute(Read(fixture.Project.Id, root), Actor, CancellationToken.None));
        Assert.AreEqual(0, initial.Revision, "Production must not read the independent session draft.");
        Assert.IsTrue(initial.SubtitleOnly);
        Assert.IsFalse(initial.SupportsManualLayout);
        Assert.IsNull(initial.ManualSelection);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Unavailable, registrations.Execute(
            Apply(initial, FullFrameManualOperation(initial)), Actor, CancellationToken.None).Status);
        BridgeRequest edit = Apply(initial, TextEdit(initial.Subtitles[0].History, "再起動後も同じ字幕修正版。"));
        LayoutSubtitleBridgeOperationResult committedResult = registrations.Execute(edit, Actor, CancellationToken.None);
        LayoutSubtitleBridgeSnapshot child = Available(committedResult);
        Assert.AreEqual(root.Id, child.CompositionVersion.ParentVersionId);
        Assert.AreEqual(1, child.Revision);
        Assert.HasCount(2, child.Subtitles[0].History.Revisions);
        Assert.IsTrue(child.SubtitleOnly);
        Assert.IsNull(child.ManualSelection);
        Assert.IsEmpty(child.Keyframes);
        var store = new SubtitleEditStore(fixture.DatabasePath);
        StoredSubtitleEdit committed = store.ReadBranch(fixture.Project.Id, root.Id).Single();
        Assert.AreEqual(edit.RequestId, committed.RequestId);
        Assert.IsNotNull(committedResult.DurableSubtitles);
        Assert.AreEqual(root.Id, committedResult.DurableSubtitles.RootVersionId);
        Assert.AreEqual(child.CompositionVersion.Id, committedResult.DurableSubtitles.HeadVersionId);
        Assert.AreEqual(child.CompositionVersion.ContentDigest, committedResult.DurableSubtitles.HeadVersionDigest);
        Assert.AreEqual(committed.Sequence, committedResult.DurableSubtitles.Sequence);
        Assert.AreEqual(committed.RecordDigest, committedResult.DurableSubtitles.JournalDigest);
        using (JsonDocument ack = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(edit, committedResult)))
        {
            JsonElement result = ack.RootElement.GetProperty("result");
            Assert.AreEqual("persisted", result.GetProperty("snapshot").GetProperty("persistence").GetString());
            Assert.AreEqual(root.Id.ToString(), result.GetProperty("durableSubtitles").GetProperty("rootVersionId").GetString());
            Assert.IsFalse(result.GetProperty("snapshot").TryGetProperty("durableSubtitles", out _));
        }
        Assert.AreEqual(CompositionSnapshotJson.WriteVersion(child.CompositionVersion), committed.ChildCanonicalJson);
        Assert.AreEqual(committed.ChildCanonicalJson, fixture.Services.Projects.GetCompositionVersion(child.CompositionVersion.Id)!.CanonicalJson,
            "An available edit response must already have its child and journal committed.");
        SubtitleEditCapture captured = registrations.CaptureSubtitles(fixture.Project.Id, [root.Id]).Single();
        Assert.AreEqual(committed.ResultSnapshotDigest, captured.HeadSnapshotDigest);
        Assert.AreEqual(1, captured.Sequence);
        Assert.AreEqual(root.Id, before.Single(capture => capture.RootVersionId == root.Id).HeadVersionId);
        Assert.AreSame(session, registrations.LayoutSubtitles.GetLatest(fixture.Project.Id, root.Id));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Unavailable, registrations.Execute(
            Apply(child, FullFrameManualOperation(child)), Actor, CancellationToken.None).Status);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, registrations.Execute(
            Apply(initial, TextEdit(initial.Subtitles[0].History, "旧版からの別編集。")), Actor, CancellationToken.None).Status);

        var restartedServices = new SourceOnlyApplicationServices(fixture.DatabasePath, fixture.Root,
            new AssetCandidateRegistry(), new DefaultSettingsStore(fixture.DatabasePath));
        SourceOnlyApplicationRegistrations restarted = restartedServices.Registrations;
        Assert.AreNotSame(fixture.Services.SubtitleEdits, restartedServices.SubtitleEdits);
        Assert.AreSame(restartedServices.SubtitleEdits, restarted.DurableSubtitles);
        await fixture.RegisterThroughCompositionAsync(restarted);
        SourceOnlyGeneratedRuntimeEvidence replay = fixture.BuildGenerated();
        await restarted.Callbacks.GeneratedReady!(replay, CancellationToken.None);
        await restarted.Callbacks.GeneratedReady(replay, CancellationToken.None);
        SubtitleEditCapture restored = restarted.CaptureSubtitles(fixture.Project.Id, [root.Id]).Single();
        Assert.AreEqual(captured.RootVersionId, restored.RootVersionId);
        Assert.AreEqual(captured.HeadVersionId, restored.HeadVersionId);
        Assert.AreEqual(captured.InitialSnapshotDigest, restored.InitialSnapshotDigest);
        Assert.AreEqual(captured.HeadSnapshotDigest, restored.HeadSnapshotDigest);
        Assert.AreEqual(committed.RecordDigest, restored.Edits.Single().RecordDigest);
        LayoutSubtitleBridgeSnapshot restoredChild = Available(restarted.Execute(Read(fixture.Project.Id, child.CompositionVersion), Actor, CancellationToken.None));
        Assert.AreEqual(CompositionSnapshotJson.WriteVersion(child.CompositionVersion), CompositionSnapshotJson.WriteVersion(restoredChild.CompositionVersion));
        CollectionAssert.AreEqual(child.Subtitles[0].History.Revisions.Select(revision => revision.Id).ToArray(),
            restoredChild.Subtitles[0].History.Revisions.Select(revision => revision.Id).ToArray());
        Assert.AreEqual("再起動後も同じ字幕修正版。", restoredChild.Subtitles[0].History.CurrentRevision.Snapshot.Text);
        Assert.IsTrue(restoredChild.SubtitleOnly);
        Assert.IsFalse(restoredChild.SupportsManualLayout);
        Assert.IsNull(restoredChild.ManualSelection);
        Assert.AreEqual(root.Id, Available(restarted.Execute(Read(fixture.Project.Id, root), Actor, CancellationToken.None)).CompositionVersion.Id);
        BridgeRequest discover = Read(fixture.Project.Id, root, latestInBranch: true);
        LayoutSubtitleBridgeOperationResult discovered = restarted.Execute(discover, Actor, CancellationToken.None);
        Assert.AreEqual(child.CompositionVersion.Id, Available(discovered).CompositionVersion.Id);
        Assert.IsTrue(discovered.Snapshot!.SubtitleOnly);
        Assert.IsNull(discovered.Snapshot.ManualSelection);
        Assert.IsNotNull(discovered.DurableSubtitles);
        Assert.AreEqual(root.Id, discovered.DurableSubtitles.RootVersionId);
        Assert.AreEqual(committed.RecordDigest, discovered.DurableSubtitles.JournalDigest);
        using (JsonDocument reloaded = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(discover, discovered)))
        {
            JsonElement result = reloaded.RootElement.GetProperty("result");
            Assert.AreEqual("persisted", result.GetProperty("snapshot").GetProperty("persistence").GetString());
            Assert.AreEqual(child.CompositionVersion.Id.ToString(), result.GetProperty("durableSubtitles").GetProperty("headVersionId").GetString());
        }
        Assert.AreEqual(child.CompositionVersion.Id, Available(restarted.Execute(edit, Actor, CancellationToken.None)).CompositionVersion.Id);
        Assert.HasCount(1, store.ReadBranch(fixture.Project.Id, root.Id), "An exact request retry must not create another child.");
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, restarted.Execute(
            Read(fixture.Project.Id, child.CompositionVersion, ContentDigest.Compute([255])), Actor, CancellationToken.None).Status);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, restarted.Execute(
            Apply(restoredChild, TextEdit(restoredChild.Subtitles[0].History, "版の指定なし。"), pinned: false), Actor, CancellationToken.None).Status);
        IReadOnlyList<SubtitleEditCapture> all = restarted.CaptureSubtitles(fixture.Project.Id, roots);
        Assert.IsTrue(all.Where(capture => capture.RootVersionId != root.Id).All(capture => capture.Sequence == 0 && capture.HeadVersionId == capture.RootVersionId));
        BridgeRequest defaultRead = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutRead,
            JsonSerializer.SerializeToElement(new { projectId = fixture.Project.Id.ToString(), evaluationTime = new { value = "1", rate = "1" } }));
        Assert.AreEqual(root.Id == fixture.Preparation.Child.Id ? child.CompositionVersion.Id : fixture.Preparation.Child.Id,
            Available(restarted.Execute(defaultRead, Actor, CancellationToken.None)).CompositionVersion.Id);
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(edit, LayoutSubtitleBridgeOperationResult.Available(restoredChild)));
        JsonElement json = response.RootElement.GetProperty("result").GetProperty("snapshot");
        Assert.IsTrue(json.GetProperty("subtitleOnly").GetBoolean());
        Assert.AreEqual(generated.Versions[root.Id].Layout.Id.ToString(), json.GetProperty("layout").GetProperty("layoutSpecId").GetString());
        Assert.AreEqual("notPersisted", json.GetProperty("persistence").GetString(), "The legacy layout contract must not claim manual-layout persistence.");
        Assert.AreEqual(executionDigest, fixture.Services.Executions.Get(fixture.Execution.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(jobRevision, fixture.Services.Jobs.GetJobAggregate(fixture.Execution.Request.RootJobId)!.Revision);
        Assert.AreEqual(workingCopy.Revision, fixture.Versions.GetCurrentState(fixture.Project.Id)!.Revision);
        Assert.AreEqual(CompositionSnapshotJson.WriteWorkingCopy(workingCopy.WorkingCopy),
            CompositionSnapshotJson.WriteWorkingCopy(fixture.Versions.GetCurrentState(fixture.Project.Id)!.WorkingCopy));
        Assert.AreEqual(0, fixture.WorkCalls);
    }

    [TestMethod]
    public async Task CachedGeneratedCallbackStillValidatesTheDurableRootInsteadOfSkippingReplay()
    {
        await using var fixture = new Fixture(0);
        SourceOnlyApplicationRegistrations registrations = fixture.Services.Registrations;
        SourceOnlyGeneratedRuntimeEvidence generated = await fixture.CheckpointGeneratedAsync(registrations);
        CompositionVersion root = generated.Preparation.Child;
        JsonNode changed = JsonNode.Parse(CompositionSnapshotJson.WriteVersion(root))!;
        changed["changeReason"] = "Different stored root metadata with identical composition elements.";
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE composition_versions SET payload = $payload WHERE id = $root;";
            command.Parameters.AddWithValue("$payload", Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(changed.ToJsonString())));
            command.Parameters.AddWithValue("$root", root.Id.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }
        // The supplied generated evidence and host cache key are unchanged. The
        // callback itself (not a later read/capture) must still invoke the journal.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => registrations.Callbacks.GeneratedReady!(fixture.BuildGenerated(), CancellationToken.None));
        Assert.IsEmpty(new SubtitleEditStore(fixture.DatabasePath).ReadBranch(fixture.Project.Id, root.Id));
        Assert.AreEqual(0, fixture.WorkCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedStageRestoreRegistersAllActualVersionsWithoutAdvancingJobsExecutionOrWorkingCopy(bool bindFailedResult)
    {
        await using var fixture = new Fixture(3);
        await fixture.SaveStagesAsync();
        SourceOnlyGeneratedRuntimeEvidence generated = fixture.BuildGenerated();
        await fixture.Pipeline.CheckpointGeneratedRuntimeAsync(fixture.Execution, generated, CancellationToken.None);
        fixture.Fail(bindFailedResult);
        ContentDigest stateDigest = fixture.Execution.StateDigest;
        WorkingCopyState before = fixture.Versions.GetCurrentState(fixture.Project.Id)!;
        var jobBefore = fixture.Services.Jobs.GetJobAggregate(fixture.Execution.Request.RootJobId)!;
        var registrations = new SourceOnlyApplicationRegistrations(new TermStore(fixture.DatabasePath));
        await fixture.RestartAsync(registrations.Callbacks);

        // Starts after production's ingest/source validation, matching the pipeline's
        // stage-only seam; no synthetic ingest completion or approved source is made.
        await fixture.Pipeline.RestoreRuntimeStagesAsync(fixture.Execution, fixture.Project, fixture.Import, CancellationToken.None);
        await fixture.Pipeline.RestoreRuntimeStagesAsync(fixture.Execution, fixture.Project, fixture.Import, CancellationToken.None);
        foreach (var item in generated.Versions.Values)
        {
            LayoutSubtitleBridgeSnapshot restored = Available(registrations.Execute(Read(fixture.Project.Id, item.Version), Actor, CancellationToken.None));
            CollectionAssert.AreEqual(item.Histories.Select(history => history.CurrentRevision.Id).ToArray(),
                restored.Subtitles.Select(cue => cue.History.CurrentRevision.Id).ToArray());
            CollectionAssert.AreEqual(item.Histories.Select(history => history.CurrentRevision.ContentDigest).ToArray(),
                restored.Subtitles.Select(cue => cue.History.CurrentRevision.ContentDigest).ToArray());
            Assert.IsTrue(restored.Subtitles.All(cue => cue.History.CurrentRevision.CreatedAt == generated.CreatedAt));
        }
        Assert.AreEqual(stateDigest, fixture.Services.Executions.Get(fixture.Execution.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, fixture.Services.Executions.Get(fixture.Execution.Request.ExecutionId)!.Phase);
        Assert.AreEqual(before.Revision, fixture.Versions.GetCurrentState(fixture.Project.Id)!.Revision);
        Assert.AreEqual(CompositionSnapshotJson.WriteWorkingCopy(before.WorkingCopy),
            CompositionSnapshotJson.WriteWorkingCopy(fixture.Versions.GetCurrentState(fixture.Project.Id)!.WorkingCopy));
        Assert.AreEqual(jobBefore.Revision, fixture.Services.Jobs.GetJobAggregate(fixture.Execution.Request.RootJobId)!.Revision);
        Assert.AreEqual(0, fixture.WorkCalls);
    }

    [TestMethod]
    public async Task FailureBeforeGeneratedCheckpointDoesNotPublishAnEmptySubstituteLayout()
    {
        await using var fixture = new Fixture(0);
        await fixture.SaveStagesAsync();
        fixture.Fail(bindResult: false);
        var registrations = new SourceOnlyApplicationRegistrations(new TermStore(fixture.DatabasePath));
        await fixture.RestartAsync(registrations.Callbacks);
        await fixture.Pipeline.RestoreRuntimeStagesAsync(fixture.Execution, fixture.Project, fixture.Import, CancellationToken.None);
        Assert.AreEqual(fixture.Import.Revision.Id, registrations.Transcript.GetRequired(fixture.Project.Id).Revision.Id);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, registrations.Execute(Read(fixture.Project.Id, fixture.Preparation.Child), Actor, CancellationToken.None).Status);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, fixture.Services.Executions.Get(fixture.Execution.Request.ExecutionId)!.Phase);
        Assert.AreEqual(0, fixture.WorkCalls);
    }

    [TestMethod]
    public void SharedSchemaKeepsLegacyRequiredFieldsAndPairsOptionalVersionPins()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        foreach (string name in new[] { "layoutReadParams", "layoutApplyParams" })
        {
            JsonElement parameters = definitions.GetProperty(name);
            Assert.IsFalse(parameters.GetProperty("additionalProperties").GetBoolean());
            Assert.AreEqual("#/$defs/entityId", parameters.GetProperty("properties").GetProperty("expectedVersionId").GetProperty("$ref").GetString());
            Assert.AreEqual("#/$defs/contentDigest", parameters.GetProperty("properties").GetProperty("expectedVersionDigest").GetProperty("$ref").GetString());
            Assert.AreEqual("expectedVersionDigest", parameters.GetProperty("dependentRequired").GetProperty("expectedVersionId")[0].GetString());
            Assert.AreEqual("expectedVersionId", parameters.GetProperty("dependentRequired").GetProperty("expectedVersionDigest")[0].GetString());
            Assert.IsFalse(parameters.GetProperty("required").EnumerateArray().Any(field => field.GetString() is "expectedVersionId" or "expectedVersionDigest"));
        }
        Assert.IsTrue(definitions.GetProperty("layoutSnapshot").GetProperty("properties").GetProperty("subtitleOnly").GetProperty("const").GetBoolean());
        CollectionAssert.AreEqual(new[] { "notPersisted", "persisted" }, definitions.GetProperty("layoutSnapshot").GetProperty("properties")
            .GetProperty("persistence").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
        JsonElement read = definitions.GetProperty("layoutReadParams");
        Assert.IsTrue(read.GetProperty("properties").GetProperty("latestInBranch").GetProperty("const").GetBoolean());
        CollectionAssert.AreEquivalent(new[] { "expectedVersionId", "expectedVersionDigest" }, read.GetProperty("dependentRequired")
            .GetProperty("latestInBranch").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.IsFalse(read.GetProperty("required").EnumerateArray().Any(field => field.GetString() == "latestInBranch"));
        Assert.IsFalse(definitions.GetProperty("layoutApplyParams").GetProperty("properties").TryGetProperty("latestInBranch", out _));
        Assert.IsFalse(definitions.GetProperty("layoutSnapshot").GetProperty("properties").TryGetProperty("durableSubtitles", out _));
        JsonElement receipt = definitions.GetProperty("layoutDurableSubtitles");
        Assert.IsFalse(receipt.GetProperty("additionalProperties").GetBoolean());
        string[] fields = ["rootVersionId", "headVersionId", "headVersionDigest", "sequence", "journalDigest"];
        CollectionAssert.AreEquivalent(fields, receipt.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        CollectionAssert.AreEquivalent(fields, receipt.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("null", receipt.GetProperty("then").GetProperty("properties").GetProperty("journalDigest").GetProperty("type").GetString());
        Assert.AreEqual("#/$defs/contentDigest", receipt.GetProperty("else").GetProperty("properties").GetProperty("journalDigest").GetProperty("$ref").GetString());
        JsonElement available = definitions.GetProperty("layoutResult").GetProperty("oneOf")[0];
        Assert.AreEqual("#/$defs/layoutDurableSubtitles", available.GetProperty("properties").GetProperty("durableSubtitles").GetProperty("$ref").GetString());
        CollectionAssert.AreEquivalent(new[] { "status", "snapshot" }, available.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.IsFalse(definitions.GetProperty("layoutResult").GetProperty("oneOf")[1].GetProperty("properties").TryGetProperty("durableSubtitles", out _));
    }

    [TestMethod]
    public async Task HostReadDoesNotSkipRestoreMerelyBecauseTheExecutionFailed()
    {
        await using var fixture = new Fixture(0);
        fixture.Fail(bindResult: false);
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(fixture.Services,
            _ => Task.FromResult(new SourceOnlyApplicationSession(fixture.Pipeline, new NoResources())));
        Assert.IsNull((await runtime.RecoverAsync()).FailureCode);
        Assert.AreSame(fixture.Pipeline, runtime.ReadyPipeline);
        // The pipeline test constructor deliberately has no native runtime options.
        // Entering its restore method must therefore fail, rather than falsely pass
        // by skipping Failed. The successful stage restoration is tested above.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RestoreAfterReadAsync(fixture.Project.Id, CancellationToken.None));
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, fixture.Services.Executions.Get(fixture.Execution.Request.ExecutionId)!.Phase);
        Assert.AreEqual(0, fixture.WorkCalls);
    }

    private static LayoutSubtitleBridgeSnapshot Available(LayoutSubtitleBridgeOperationResult result)
    {
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, result.Status);
        Assert.IsNotNull(result.Snapshot);
        return result.Snapshot;
    }

    private static BridgeRequest Read(EntityId<ProjectIdTag> projectId, CompositionVersion version, ContentDigest? digest = null,
        bool latestInBranch = false)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["projectId"] = projectId.ToString(),
            ["evaluationTime"] = null,
            ["expectedVersionId"] = version.Id.ToString(),
            ["expectedVersionDigest"] = (digest ?? version.ContentDigest).Hex,
        };
        if (latestInBranch) { parameters["latestInBranch"] = true; }
        return BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutRead, JsonSerializer.SerializeToElement(parameters));
    }

    private static BridgeRequest Apply(LayoutSubtitleBridgeSnapshot snapshot, object operation, bool pinned = true)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["projectId"] = snapshot.ProjectId.ToString(),
            ["expectedRevision"] = snapshot.Revision,
            ["operation"] = operation,
        };
        if (pinned)
        {
            parameters["expectedVersionId"] = snapshot.CompositionVersion.Id.ToString();
            parameters["expectedVersionDigest"] = snapshot.CompositionVersion.ContentDigest.Hex;
        }
        return BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(parameters));
    }

    private static object TextEdit(SubtitleCueHistory history, string text) => new
    {
        kind = "editSubtitle",
        cueId = history.CueId.ToString(),
        expectedRevisionId = history.CurrentRevision.Id.ToString(),
        expectedRevisionDigest = history.CurrentRevision.ContentDigest.Hex,
        edit = new { kind = "text", text },
        reason = "Explicit editor correction on the selected generated version.",
    };

    private static object FullRectDto() => new
    {
        x = new { numerator = "0", denominator = "1" },
        y = new { numerator = "0", denominator = "1" },
        width = new { numerator = "1", denominator = "1" },
        height = new { numerator = "1", denominator = "1" },
    };

    private static object FullFrameManualOperation(LayoutSubtitleBridgeSnapshot snapshot) => new
    {
        kind = "setManualLayout",
        mode = "singleSubjectCrop",
        staticCropDecision = "singleStaticCropSufficient",
        sources = new[] { new { sourceAssetId = snapshot.Sources[0].SourceAssetId.ToString(), crop = FullRectDto(), subject = FullRectDto(), destination = FullRectDto() } },
        templateId = (string?)null,
    };

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) { directory = directory.Parent; }
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class NoResources : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Fixture(int shortCount, int sourceWidth = 1920, int sourceHeight = 1080, bool empty = false, bool realisticEnglish = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "HubRadio.GeneratedSubtitleRegistration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "store.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(Root, "backups"));
            Profile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            Settings = Profile.CreateSnapshot();
            var settingsStore = new DefaultSettingsStore(DatabasePath);
            _ = settingsStore.RegisterProfile(Encoding.UTF8.GetBytes(Profile.CanonicalJson));
            Services = new SourceOnlyApplicationServices(DatabasePath, Root, new AssetCandidateRegistry(), settingsStore);
            LegacyRegistrations = new SourceOnlyApplicationRegistrations(new TermStore(DatabasePath));
            Versions = new VersionService(Services.Projects);
            var audio = new AnalysisAudioTimeline(EntityId<ProjectIdTag>.New(), EntityId<JobIdTag>.New(), EntityId<AssetIdTag>.New(),
                ContentDigest.Compute([1, 2, 3]), 100, EntityId<ArtifactIdTag>.New(), ContentDigest.Compute([4, 5, 6]), 0, 1,
                new RationalTime(1, 48000), default, 0, 1024, EntityId<ArtifactIdTag>.New(), ContentDigest.Compute([7, 8, 9]),
                1920044, 960000, EntityId<ArtifactIdTag>.New());
            AsrWorkerSegment[] segments = empty ? [] : realisticEnglish ? RealisticEnglishSegments() : Enumerable.Range(0, 3).Select(ordinal =>
            {
                double start = 2 + ordinal * 20;
                return new AsrWorkerSegment(ordinal, 0, "実測。 確認。 結論。", start, start + 15, -0.1, 0.01,
                    [new AsrWorkerToken("実測。", start, start + 2, 0.9, false),
                     new AsrWorkerToken("確認。", start + 6, start + 8, 0.9, false),
                     new AsrWorkerToken("結論。", start + 13, start + 15, 0.9, false)]);
            }).ToArray();
            var worker = new AsrWorkerResult("Systran/faster-whisper-small", "536b0662742c02347bc0e980a01041f333bce120",
                new ContentDigest("4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"),
                new AsrWorkerConfiguration("cpu", "int8", realisticEnglish ? LocalAnalysisWorker.AutoLanguagePolicy : "ja", "transcribe", 5, true, false, true),
                audio.AnalysisAudioDigest, audio.AnalysisAudioByteSize, 60, realisticEnglish ? "en" : "ja", 0.9, 60, segments);
            Import = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(worker, audio,
                EntityId<TranscriptRevisionIdTag>.New(), EntityId<TranscriptSpeakerIdTag>.New(), At));
            var index = new PtsIndex(audio.SourceAssetId, audio.ProjectId, EntityId<JobIdTag>.New(), "source.mov", audio.SourceContentDigest,
                audio.SourceByteSize, 0, new RationalTime(1, 30), Enumerable.Range(0, 1800)
                    .Select(frame => new PtsIndexEntry(frame, new RationalTime(frame, 30), new RationalTime(1, 30), frame == 0)).ToArray());
            SourceSpan full = SourceOnlyPipeline.CreateFullSourceSpan(index, Import);
            CompositionElement[] elements = realisticEnglish
                // What the automatic silence cuts leave: the lead-in silence and the pause between the sentences are gone.
                ? [new CompositionElement(EntityId<CompositionElementIdTag>.New(), new TimeRange(default, new RationalTime(111, 30)),
                        OriginRef.FromSourceSpan(full.Id), sourceRange: new TimeRange(new RationalTime(12, 30), new RationalTime(111, 30))),
                    new CompositionElement(EntityId<CompositionElementIdTag>.New(), new TimeRange(new RationalTime(111, 30), new RationalTime(125, 30)),
                        OriginRef.FromSourceSpan(full.Id), sourceRange: new TimeRange(new RationalTime(139, 30), new RationalTime(125, 30)))]
                : [new CompositionElement(EntityId<CompositionElementIdTag>.New(), new TimeRange(default, full.Range.Duration),
                    OriginRef.FromSourceSpan(full.Id), sourceRange: full.Range)];
            var parent = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), audio.ProjectId, null, Actor, At,
                "Synthetic machine-source fixture; no media approval", RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
            var copy = new WorkingCopy(EntityId<WorkingCopyIdTag>.New(), parent.ProjectId, parent.Id, At, At, parent.Elements);
            BundledRenderProfileDefinition landscape = StandardRenderProfileFactory.CreateLandscape(Profile, Settings,
                BundledRenderProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json"))), new RationalNumber(30, 1));
            var draft = new ProjectCreationDraft(EntityId<ProjectCreationDraftIdTag>.New(), audio.SourceAssetId, "source", "test", "test",
                PublicationScope.InternalOnly, [new ProjectOutputProfileReference(landscape.Profile.Id, "long-form", landscape.Profile.ProfileDigest)],
                "ja-JP", ["Host"], new ProjectDeadlineSelection(null), landscape.TemplateReference);
            string draftJson = CanonicalJson.Canonicalize(JsonSerializer.Serialize(draft));
            _ = Services.Drafts.CreateDraft(draft, draftJson, At);
            var emptyProject = new Project(parent.ProjectId, "source", "test", "test", PublicationScope.InternalOnly, null, [], []);
            var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), parent.ProjectId, draft.Id,
                audio.SourceAssetId, audio.AnalysisJobId, index.JobId, Settings, Actor, At);
            Execution = Services.Drafts.FinalizeSourceOnlyWithExecution(draft, emptyProject, draftJson, 0, At, request);
            var asset = new Asset(audio.SourceAssetId, "source.mov", "video/quicktime", audio.SourceContentDigest, audio.SourceByteSize,
                new RightsRecord(RightsStatus.Unconfirmed, null, "Synthetic source only; not permission to render or deliver", false));
            Project = new Project(parent.ProjectId, "source", "test", "test", PublicationScope.InternalOnly, null, [asset], []);
            string projectJson = ProjectSnapshotJson.WriteProject(Project);
            _ = Services.Projects.UpdateProject(Project, projectJson, 0, At);
            Services.Projects.AppendCompositionVersion(parent, CompositionSnapshotJson.WriteVersion(parent));
            Services.Projects.CreateWorkingCopy(copy, CompositionSnapshotJson.WriteWorkingCopy(copy));
            Pipeline = NewPipeline(null);
            Analysis = Pipeline.BuildAnalysis(Project, Import, Execution, At.AddSeconds(1));
            var template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), landscape.TemplateReference, BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
            LongFormElementInput Omitted(LongFormElementKind kind) => new(EntityId<LongFormElementIdTag>.New(), kind, false, null, null, [], []);
            var longForm = new LongFormCompositionRequest(EntityId<LongFormPlanIdTag>.New(), Project,
                new LongFormProvenanceSnapshot(Project.Id, Import.Revision.Id, ContentDigest.Compute(Encoding.UTF8.GetBytes(
                    TranscriptSnapshotJson.WriteRevision(Import.Revision))), Import.SourceSpans.Append(full).ToArray(), []), template, landscape,
                LongFormDestination.InternalDistribution, null, Omitted(LongFormElementKind.PreRollMargin), Omitted(LongFormElementKind.Opening),
                Omitted(LongFormElementKind.ProgramName), Omitted(LongFormElementKind.EpisodeNumber), Omitted(LongFormElementKind.EpisodeTitle),
                Omitted(LongFormElementKind.PerformerIntroduction), Omitted(LongFormElementKind.PublicationScopeIndicator), Omitted(LongFormElementKind.Body), [],
                Omitted(LongFormElementKind.Summary), [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omitted(LongFormElementKind.CallToAction))],
                Omitted(LongFormElementKind.Ending), Omitted(LongFormElementKind.PostRollMargin));
            EmphasisSuggestionCandidate[] candidates = Import.SourceSpans.Take(shortCount).Select(span =>
            {
                var suggestion = new Suggestion(EntityId<SuggestionIdTag>.New(), EntityId<ModelRunIdTag>.New(), SuggestionKind.Highlight,
                    SuggestionStatus.Proposed, OriginRef.FromSourceSpan(span.Id), "Select the actual synthetic utterance range", null, null, null);
                return SuggestionService.Register(new EmphasisCandidate(suggestion, span, Import.Revision.Id,
                    longForm.Provenance.TranscriptRevisionDigest, Analysis.TopicMetadata.Single().Id, EmphasisCandidateService.RuleVersion,
                    Import.Revision.Utterances.Single(utterance => utterance.SourceSpanId == span.Id).Text,
                    EmphasisTextSourceKind.OriginalTranscript, [EmphasisReasonKind.ImportantStatement], []), []);
            }).ToArray();
            Composition = StandardCompositionOrchestrator.Apply(Versions, new StandardCompositionOrchestrationRequest(parent, copy, 0,
                new StandardProjectStoreState(1, ContentDigest.Compute(Encoding.UTF8.GetBytes(projectJson)), At), Profile, Settings,
                File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")),
                File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/9x16/vertical-short.json")), longForm, EntityId<TimelineMapIdTag>.New(),
                new SourceDisplayGeometry(sourceWidth, sourceHeight), new RationalNumber(30, 1), ContentDigest.Compute([10]), ContentDigest.Compute([11]),
                new MusicPlanningService().Plan(MusicCatalog.CreateUnavailable(MusicLibraryState.Absent, MusicCatalogWarning.LibraryAbsent), [], MusicPlanningOptions.Default),
                Analysis.TopicMetadata, candidates, [], Actor, At.AddSeconds(2), "Create actual plan from synthetic unapproved source evidence"));
            Assert.HasCount(shortCount, Preparation.Plan.ShortForms);
        }

        /// <summary>The real faster-whisper shape: 18 English words in ~8 s, word times, every token but the first with a leading space.</summary>
        private static AsrWorkerSegment[] RealisticEnglishSegments()
        {
            string[] words = ["Hello", "and", "welcome", "to", "the", "Hub", "Radio", "movie", "creator.", "Today", "we", "are",
                "testing", "the", "speech", "recognition", "in", "English."];
            AsrWorkerSegment Segment(int ordinal, int from, int count)
            {
                // A 0.6 s pause between the two sentences.
                static double Start(int i) => 0.5 + i * 0.4 + (i >= 9 ? 0.6 : 0);
                AsrWorkerToken[] tokens = Enumerable.Range(from, count).Select(i => new AsrWorkerToken(
                    (i == from ? "" : " ") + words[i], Start(i), Start(i) + 0.36, 0.9, false)).ToArray();
                return new AsrWorkerSegment(ordinal, 0, string.Concat(tokens.Select(static t => t.Text)).Trim(),
                    tokens[0].StartSeconds!.Value, tokens[^1].EndSeconds!.Value, -0.1, 0.01, tokens);
            }
            return [Segment(0, 0, 18)];
        }

        internal string Root { get; }
        internal string DatabasePath { get; }
        internal SourceOnlyApplicationServices Services { get; }
        internal SourceOnlyApplicationRegistrations LegacyRegistrations { get; }
        internal VersionService Versions { get; }
        internal Project Project { get; }
        internal DefaultSettingsProfile Profile { get; }
        internal DefaultSettingsSnapshot Settings { get; }
        internal TranscriptImportResult Import { get; }
        internal SourceOnlyAnalysisEvidence Analysis { get; }
        internal SourceOnlyPipeline Pipeline { get; private set; }
        internal StoredSourceOnlyExecution Execution { get; private set; }
        internal StandardCompositionApplication Composition { get; }
        internal StandardCompositionPreparation Preparation => Composition.Preparation;
        internal int WorkCalls { get; private set; }

        internal SourceOnlyGeneratedRuntimeEvidence BuildGenerated(DateTimeOffset? createdAt = null) =>
            SourceOnlyPipeline.BuildGeneratedRuntimeEvidence(Preparation, Import, Profile, Settings,
                new RenderFontEvidence("Noto Sans JP", ContentDigest.Compute([8, 9])), Actor, createdAt ?? At.AddSeconds(3));

        internal async Task RegisterThroughCompositionAsync(SourceOnlyApplicationRegistrations registrations)
        {
            await registrations.Callbacks.TranscriptReady!(Project, Import, CancellationToken.None);
            await registrations.Callbacks.AnalysisReady!(Project, Import, Analysis, CancellationToken.None);
            await registrations.Callbacks.CompositionReady!(Preparation, Import, CancellationToken.None);
        }

        internal async Task<SourceOnlyGeneratedRuntimeEvidence> RegisterGeneratedAsync(SourceOnlyApplicationRegistrations registrations)
        {
            await RegisterThroughCompositionAsync(registrations);
            SourceOnlyGeneratedRuntimeEvidence generated = BuildGenerated();
            await registrations.Callbacks.GeneratedReady!(generated, CancellationToken.None);
            return generated;
        }

        internal async Task<SourceOnlyGeneratedRuntimeEvidence> CheckpointGeneratedAsync(SourceOnlyApplicationRegistrations registrations)
        {
            await RegisterThroughCompositionAsync(registrations);
            SourceOnlyGeneratedRuntimeEvidence generated = BuildGenerated();
            int callbacks = 0;
            await RestartAsync(new SourceOnlyRuntimeCallbacks
            {
                GeneratedReady = (evidence, token) =>
                {
                    foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in evidence.Versions.Values)
                    {
                        Assert.AreEqual(CompositionSnapshotJson.WriteVersion(item.Version),
                            Services.Projects.GetCompositionVersion(item.Version.Id)?.CanonicalJson,
                            "Every real generated root must be saved BEFORE the host callback, including selected shorts.");
                    }
                    callbacks++;
                    return registrations.Callbacks.GeneratedReady!(evidence, token);
                },
            });
            await Pipeline.CheckpointGeneratedRuntimeAsync(Execution, generated, CancellationToken.None);
            Assert.AreEqual(1, callbacks);
            return generated;
        }

        private SourceOnlyPipeline NewPipeline(SourceOnlyRuntimeCallbacks? callbacks) => new(DatabasePath, Root, Services.Candidates,
            (_, _) => { WorkCalls++; throw new AssertFailedException("Registration and restore must not execute the pipeline."); },
            callbacks: callbacks, jobStore: Services.Jobs, jobCoordinator: Services.Coordinator);

        internal async Task RestartAsync(SourceOnlyRuntimeCallbacks callbacks)
        {
            await Pipeline.DisposeAsync();
            Pipeline = NewPipeline(callbacks);
        }

        internal async Task SaveStagesAsync()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(JsonSerializer.Serialize(Analysis)));
            string staging = Path.Combine(Root, "projects", Project.Id.ToString(), "staging");
            Directory.CreateDirectory(staging);
            string path = Path.Combine(staging, "analysis-registration-fixture.json");
            await File.WriteAllBytesAsync(path, bytes);
            var id = SourceOnlyPipeline.StableId<ArtifactIdTag>(Execution.Request.ExecutionId.ToString(), "analysis-evidence");
            _ = await Services.Artifacts.PromoteAsync(new ArtifactPromotionRequest(id, Project.Id, ArtifactKind.Analysis, path,
                "source-only/" + id + ".analysis-evidence.json", ContentDigest.Compute(bytes), bytes.LongLength, At.AddSeconds(3)));
            await Pipeline.SaveCompositionStageAsync(Execution, "composition-checkpoint", Composition.Checkpoint, CancellationToken.None);
        }

        internal void Fail(bool bindResult)
        {
            var running = new JobAttempt(EntityId<JobAttemptIdTag>.New(), Execution.Request.RootJobId, 1, JobState.Running,
                At, At.AddSeconds(4), null, null);
            _ = Services.Jobs.StartAttempt(running.JobId, 0, running, At.AddSeconds(4));
            _ = Services.Jobs.CompleteAttempt(running.JobId, 1, new JobAttempt(running.Id, running.JobId, 1, JobState.Failed,
                At, running.StartedAt, At.AddSeconds(5), "job.execution_failed"), At.AddSeconds(5));
            _ = Services.Jobs.CancelQueued(Execution.Request.IngestJobId, 0, At.AddSeconds(5));
            Execution = Services.Executions.UpdateState(Execution.Request.ExecutionId, Execution.Revision, SourceOnlyExecutionPhase.Evaluating,
                versionId: bindResult ? Preparation.Child.Id : null, digest: bindResult ? Preparation.Child.ContentDigest : null);
            SourceOnlyResult? result = bindResult ? new SourceOnlyResult(new SourceOnlyResultBinding(Project.Id, Execution.Request.ExecutionId,
                Execution.Request.RootJobId, Settings.ContentDigest, Preparation.Child.Id, Preparation.Child.ContentDigest),
                StandardArtifactGenerationDisposition.Failed, [], [], [new SourceOnlyResultFailure(SourceOnlyResultFailureCode.GenerationFailed)], [], [], null, false) : null;
            Execution = Services.Executions.UpdateState(Execution.Request.ExecutionId, Execution.Revision, SourceOnlyExecutionPhase.Failed,
                result?.CanonicalJson, bindResult ? Preparation.Child.Id : null, bindResult ? Preparation.Child.ContentDigest : null,
                "source_only.test_stage_failed");
        }

        public async ValueTask DisposeAsync()
        {
            await Pipeline.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}