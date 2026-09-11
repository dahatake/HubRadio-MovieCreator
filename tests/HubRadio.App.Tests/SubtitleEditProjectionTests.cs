using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Profiles;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

/// <summary>
/// Synthetic MACHINE transcript + actual standard builder + real schema-012 journal.
/// No worker, model, rendering, runtime callback, fabricated approval or Human ASR.
/// These tests are intentionally not executed as part of the editing task.
/// </summary>
[TestClass]
public sealed class SubtitleEditProjectionTests
{
    private const string Editor = "S-1-5-21-111-222-333-1001";
    private const string Coordinator = "S-1-5-21-111-222-333-1002";
    private const string Reason = "Correct the actual selected subtitle after editor review.";
    private static readonly DateTimeOffset At = new(2038, 4, 5, 6, 7, 8, TimeSpan.Zero);
    private static readonly DateTimeOffset ProjectedAt = At.AddHours(3);

    [TestMethod]
    public void SourceTextCorrectionBecomesOneNewInitialWithOriginalOriginAndUntouchedAsrAndHistory()
    {
        using var fixture = new Fixture();
        string transcriptBefore = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);
        LayoutSubtitleBridgeSnapshot edited = fixture.Edit(0, new { kind = "text", text = "人手で訂正した字幕。" });
        SubtitleEditCapture capture = fixture.Capture();
        string historyBefore = SubtitleEditProjection.HistoryCanonical(edited.Subtitles[0].History);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(maximumCueDuration: 6);
        Assert.AreNotEqual(fixture.Root.Build.SettingsDigest, target.Build.SettingsDigest);
        ContentDigest targetBefore = SubtitleEditProjection.GeneratedDigest(target);

        SubtitleEditProjectionResult result = Apply(capture, target);

        SubtitleCueHistory projected = result.Output.Histories[0];
        SubtitleCueRevision manual = edited.Subtitles[0].History.CurrentRevision;
        Assert.HasCount(1, projected.Revisions);
        Assert.IsEmpty(projected.Inheritance);
        Assert.AreEqual(target.Version.Id, projected.CurrentRevision.CompositionVersionId);
        Assert.AreNotEqual(capture.HeadVersionId, projected.CurrentRevision.CompositionVersionId);
        Assert.AreNotEqual(target.Histories[0].CurrentRevision.Id, projected.CurrentRevision.Id);
        Assert.AreEqual(SubtitleCueChangeKind.Initial, projected.CurrentRevision.Change.Kind);
        Assert.IsNull(projected.CurrentRevision.Change.Previous);
        Assert.IsNull(projected.CurrentRevision.ParentRevisionId);
        Assert.AreEqual("人手で訂正した字幕。", projected.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(manual.Change.Origin, projected.CurrentRevision.Snapshot.TextOrigin);
        Assert.AreEqual(manual.Change.Origin, projected.CurrentRevision.Change.Origin);
        Assert.AreSame(target.Histories[0].SourceCandidate, projected.SourceCandidate);
        Assert.AreEqual(projected.SourceCandidate.Origin, projected.CurrentRevision.SourceOrigin);
        Assert.AreNotEqual(manual.SourceOrigin, projected.CurrentRevision.SourceOrigin);
        Assert.AreEqual("原文一。", projected.SourceCandidate.Text);
        Assert.AreEqual(Coordinator, projected.CurrentRevision.ActorSid);
        Assert.AreEqual(SubtitleEditProjection.MaterializationReason, projected.CurrentRevision.Reason);
        Assert.AreSame(target.Histories[1], result.Output.Histories[1]);
        Assert.AreSame(target.Build.Subtitles.DisplayProfile, result.Output.Build.Subtitles.DisplayProfile);
        Assert.AreSame(target.Build.Subtitles.AssProfile, result.Output.Build.Subtitles.AssProfile);
        CollectionAssert.AreEqual(target.Build.Warnings.ToArray(), result.Output.Build.Warnings.ToArray());
        CollectionAssert.AreEqual(target.Build.Subtitles.Warnings.ToArray(), result.Output.Build.Subtitles.Warnings.ToArray());
        CollectionAssert.AreEqual(result.Output.Histories.Select(history => history.CurrentRevision).ToArray(),
            result.Output.Build.Subtitles.Revisions.ToArray());
        Assert.AreEqual(targetBefore, SubtitleEditProjection.GeneratedDigest(target));
        Assert.AreEqual(historyBefore, SubtitleEditProjection.HistoryCanonical(edited.Subtitles[0].History));
        Assert.AreEqual(transcriptBefore, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        Assert.AreEqual(TranscriptChangeSource.Machine, fixture.Transcript.ChangeSource);
        Assert.IsNull(fixture.Transcript.ParentRevisionId);
        Assert.HasCount(1, fixture.Rows);
        Assert.IsNull(fixture.Projects.GetCompositionVersion(target.Version.Id), "Projection must not persist a generated version or edit.");
        CollectionAssert.AreEqual(new[] { manual.Change.Origin.EditorialRecordId! }, result.Evidence.TrustedEditorialRecordIds.ToArray());
        Assert.AreEqual(1, result.Evidence.ProjectedCueCount);
        Assert.AreEqual(0, result.Evidence.NewHumanActionCount);
        Assert.AreEqual(capture.GeneratedInputDigest, result.Evidence.SourceGeneratedInputDigest);
        Assert.AreEqual(capture.HeadSnapshotDigest, result.Evidence.SourceHeadSnapshotDigest);
        Assert.AreEqual(CanonicalJson.Canonicalize(result.Evidence.CanonicalJson), result.Evidence.CanonicalJson);
        Assert.AreEqual(Hash(result.Evidence.CanonicalJson), result.Evidence.ContentDigest);
        using JsonDocument evidence = JsonDocument.Parse(result.Evidence.CanonicalJson);
        JsonElement field = evidence.RootElement.GetProperty("cues")[0].GetProperty("fields")[0];
        Assert.AreEqual(Editor, field.GetProperty("sourceActorSid").GetString());
        Assert.AreEqual(Reason, field.GetProperty("sourceReason").GetString());
        Assert.AreEqual(SubtitleEditProjection.FieldDigest(SubtitleCueChangeKind.Text, manual.Snapshot).Hex,
            field.GetProperty("sourceFieldDigest").GetString());
        Assert.AreEqual(1, evidence.RootElement.GetProperty("source").GetProperty("recordPrefix").GetArrayLength());
    }

    [TestMethod]
    public void AThenBThenAUsesEffectiveSiblingHistoriesAndOnlyTheirFinalTypedFields()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "一度目の訂正。" });
        fixture.Edit(1, new { kind = "style", style = "emphasis" });
        LayoutSubtitleBridgeSnapshot head = fixture.Edit(0, new { kind = "text", text = "最後の訂正。" });
        Assert.HasCount(3, head.Subtitles[0].History.Revisions);
        Assert.HasCount(2, head.Subtitles[1].History.Revisions);
        Assert.IsNotEmpty(head.Subtitles[0].History.Inheritance);
        Assert.AreNotEqual(head.CompositionVersion.Id, head.Subtitles[1].History.CurrentRevision.CompositionVersionId);

        SubtitleEditProjectionResult result = Apply(fixture.Capture(), fixture.Generate());

        Assert.AreEqual("最後の訂正。", result.Output.Histories[0].CurrentRevision.Snapshot.Text);
        Assert.AreEqual(SubtitleCueStyle.Emphasis, result.Output.Histories[1].CurrentRevision.Snapshot.Style);
        Assert.AreEqual(result.Output.Histories[1].SourceCandidate.Origin, result.Output.Histories[1].CurrentRevision.Snapshot.TextOrigin);
        Assert.IsTrue(result.Output.Histories.All(history => history.Revisions.Count == 1));
        CollectionAssert.AreEquivalent(head.Subtitles.Select(binding => binding.History.CurrentRevision.Change.Origin.EditorialRecordId!).ToArray(),
            result.Evidence.TrustedEditorialRecordIds.ToArray());
        Assert.IsFalse(result.Evidence.TrustedEditorialRecordIds.Contains(head.Subtitles[0].History.Revisions[1].Change.Origin.EditorialRecordId!));
        Assert.HasCount(3, fixture.Rows);
        Assert.AreEqual(2, result.Evidence.ProjectedCueCount);
    }

    [TestMethod]
    public void TextRevertedToSourceStillRetainsTheRealLastEditorialOrigin()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "別の表記。" });
        LayoutSubtitleBridgeSnapshot head = fixture.Edit(0, new { kind = "text", text = "原文一。" });
        SubtitleEditProjectionResult result = Apply(fixture.Capture(), fixture.Generate());
        Assert.AreEqual("原文一。", result.Output.Histories[0].CurrentRevision.Snapshot.Text);
        Assert.AreEqual(head.Subtitles[0].History.CurrentRevision.Change.Origin, result.Output.Histories[0].CurrentRevision.Snapshot.TextOrigin);
        Assert.AreEqual(1, result.Evidence.ProjectedCueCount, "Actual typed history, not string inequality, decides projection.");
        Assert.AreEqual(0, result.Evidence.NewHumanActionCount);
    }

    [TestMethod]
    public void TimingUsesTheUniqueNewCutOffsetAndPlacementIsResolvedForTheNewCandidate()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, Timing(Range(3, 4, 2)));
        fixture.Edit(0, new { kind = "style", style = "quote" });
        LayoutSubtitleBridgeSnapshot head = fixture.Edit(0, fixture.Placement());
        Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, head.Subtitles[0].History.CurrentRevision.Snapshot.LayoutResolution.Status);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(cut: Range(1, 9));

        SubtitleEditProjectionResult result = Apply(fixture.Capture(), target);

        SubtitleCueRevision projected = result.Output.Histories[0].CurrentRevision;
        Assert.AreEqual(Range(1, 4, 2), projected.Snapshot.Cue.Range);
        Assert.AreEqual(target.Histories[0].SourceCandidateId, projected.SourceCandidateId);
        Assert.AreEqual(Range(1, 2), target.Histories[0].SourceCandidate.SourceRange);
        Assert.AreEqual(SubtitleCueStyle.Quote, projected.Snapshot.Style);
        Assert.AreEqual(target.Histories[0].SourceCandidate.Origin, projected.Snapshot.TextOrigin);
        Assert.AreEqual(target.Histories[0].SourceCandidateId, projected.Snapshot.LayoutResolution.SourceCandidateId);
        Assert.AreEqual(head.Subtitles[0].History.CurrentRevision.Snapshot.ResolvedPlacement!.Rect, projected.Snapshot.ResolvedPlacement!.Rect);
        Assert.AreNotEqual(head.Subtitles[0].History.CurrentRevision.Snapshot.ResolvedPlacement!.Id, projected.Snapshot.ResolvedPlacement.Id);
        Assert.AreEqual(target.Build.Subtitles.DisplayProfile.VersionDigest, projected.Snapshot.LayoutResolution.ProfileVersionDigest);
        Assert.HasCount(3, result.Evidence.TrustedEditorialRecordIds);
        StandardSubtitlePlacement.Validate(result.Output.Build.Subtitles.AssProfile, projected.Snapshot.LayoutResolution);
    }

    [TestMethod]
    public void TimingOutsideNewSingleCutOrOverlappingAnotherCueFailsWithoutPartialOutput()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, Timing(Range(1, 5, 2)));
        AssertFailure(SubtitleEditProjectionFailureReason.TimingConflict, fixture.Capture(), fixture.Generate(cut: Range(1, 9)));
        fixture.Edit(0, Timing(Range(1, 6)));
        AssertFailure(SubtitleEditProjectionFailureReason.TimingConflict, fixture.Capture(), fixture.Generate());
        Assert.HasCount(2, fixture.Rows);
    }

    [TestMethod]
    public void ManualRectangleOutsideNewSubtitleRegionFailsInsteadOfChoosingAnAutomaticPlacement()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot head = fixture.Edit(0, fixture.Placement());
        Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, head.Subtitles[0].History.CurrentRevision.Snapshot.LayoutResolution.Status);
        SubtitleLayoutRect original = fixture.Root.Histories[0].CurrentRevision.Snapshot.ResolvedPlacement!.Rect;
        LayoutSpec layout = fixture.Root.Layout;
        var narrowed = new LayoutSpec(layout.Id, layout.OutputAspectWidth, layout.OutputAspectHeight,
            layout.Regions.Select(region => region.Role == LayoutRegionRole.Subtitle
                ? new LayoutRegion(region.Name, new NormalizedRect(new RationalNumber(13, 80), original.Y,
                    new RationalNumber(27, 40), original.Height), region.Role) : region).ToArray(), layout.SafeZones);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(layout: narrowed);
        Assert.HasCount(2, target.Histories, "The target must be generated successfully before testing manual bounds.");
        AssertFailure(SubtitleEditProjectionFailureReason.LayoutConflict, fixture.Capture(), target);
    }

    [TestMethod]
    public void MissingTokenCueAndRepeatedOutputOccurrenceNeverUseTextSimilarity()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "編集を失わない。" });
        SourceOnlyGeneratedSubtitleRuntimeEvidence missing = fixture.Generate(cut: Range(5, 5));
        Assert.HasCount(1, missing.Histories);
        AssertFailure(SubtitleEditProjectionFailureReason.MissingCue, fixture.Capture(), missing);
        SourceOnlyGeneratedSubtitleRuntimeEvidence repeated = fixture.Generate(repeated: true);
        Assert.HasCount(4, repeated.Histories);
        AssertFailure(SubtitleEditProjectionFailureReason.AmbiguousCue, fixture.Capture(), repeated);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChangedSettingsCannotSplitOrMergeAnEditedSourceCue(bool initiallySplit)
    {
        using var fixture = new Fixture(splitCues: initiallySplit);
        fixture.Edit(0, new { kind = "text", text = "分割結合時に捨てない編集。" });
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(maximumCueDuration: initiallySplit ? 7 : 1);
        Assert.HasCount(initiallySplit ? 4 : 2, fixture.Root.Histories);
        Assert.HasCount(initiallySplit ? 2 : 4, target.Histories);
        AssertFailure(SubtitleEditProjectionFailureReason.MissingCue, fixture.Capture(), target);
    }

    [TestMethod]
    public void RepeatedSourceWithUnchangedUniqueOccurrenceEditsOnlyTheSelectedOccurrence()
    {
        using var fixture = new Fixture(repeated: true);
        fixture.Edit(2, new { kind = "text", text = "二度目の出現だけ訂正。" });
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(repeated: true);
        SubtitleEditProjectionResult result = Apply(fixture.Capture(), target);
        Assert.AreSame(target.Histories[0], result.Output.Histories[0]);
        Assert.AreEqual("二度目の出現だけ訂正。", result.Output.Histories[2].CurrentRevision.Snapshot.Text);
        Assert.AreEqual(Range(11, 2), result.Output.Histories[2].CurrentRevision.Snapshot.Cue.Range);
    }

    [TestMethod]
    public void RepeatedOccurrenceKeepsItsOriginalTimingClockAcrossNoEditProjections()
    {
        using var fixture = new Fixture(repeated: true);
        fixture.Edit(2, Timing(Range(23, 4, 2)));
        SubtitleEditProjectionResult first = Apply(fixture.Capture(), fixture.Generate(repeated: true));
        LayoutSubtitleBridgeSnapshot root = fixture.RegisterProjection(first);
        SubtitleEditCapture capture = fixture.CapturePrefix(root.CompositionVersion.Id, 0, null);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(repeated: true);
        SubtitleEditProjectionResult second = Apply(capture, target);
        Assert.AreEqual(Range(23, 4, 2), second.Output.Histories[2].CurrentRevision.Snapshot.Cue.Range);
        Assert.AreSame(target.Histories[0], second.Output.Histories[0]);
        Assert.AreSame(target.Histories[1], second.Output.Histories[1]);
        Assert.AreSame(target.Histories[3], second.Output.Histories[3]);
        Assert.AreEqual(first.Evidence.GetInheritedFields(first.Output.Histories[2].CueId).Single().Record.RecordDigest,
            second.Evidence.GetInheritedFields(second.Output.Histories[2].CueId).Single().Record.RecordDigest);
        AssertFailure(SubtitleEditProjectionFailureReason.AmbiguousCue, capture, fixture.Generate(repeated: true, cut: Range(1, 9)));
    }

    [TestMethod]
    [DataRow("asset")]
    [DataRow("sourceHash")]
    [DataRow("stream")]
    [DataRow("revision")]
    [DataRow("tokens")]
    public void IdenticalTextAndTimesCannotReplaceAnySourceBinding(string field)
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "正しい根拠だけに再適用。" });
        TranscriptRevision transcript = fixture.Transcript;
        SourceSpan span = fixture.Span;
        if (field is "revision" or "tokens")
        {
            Utterance old = transcript.Utterances[0];
            Token[] tokens = old.Tokens.Select(token => new Token(field == "tokens" ? EntityId<TranscriptTokenIdTag>.New() : token.Id,
                token.Text, token.Range, token.RecognitionConfidence)).ToArray();
            var utterance = new Utterance(old.Id, old.SourceSpanId, old.SpeakerId, old.SpeakerLabel, old.Text, old.Range,
                old.RecognitionConfidence, [new Sentence(old.Sentences[0].Id, old.Text, tokens)]);
            transcript = new TranscriptRevision(field == "revision" ? EntityId<TranscriptRevisionIdTag>.New() : transcript.Id,
                null, TranscriptChangeSource.Machine, transcript.CreatedAt, [utterance]);
        }
        span = new SourceSpan(span.Id, field == "asset" ? EntityId<AssetIdTag>.New() : span.AssetId,
            field == "stream" ? "audio:99" : span.StreamId, span.Range,
            field == "sourceHash" ? Hash("different media") : span.SourceDigest, transcript.Id,
            transcript.Utterances[0].Tokens.Select(token => token.Id).ToArray());
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(transcript: transcript, span: span);
        Assert.AreEqual(fixture.Root.Histories[0].SourceCandidate.Text, target.Histories[0].SourceCandidate.Text);
        AssertFailure(SubtitleEditProjectionFailureReason.MissingCue, fixture.Capture(), target);
    }

    [TestMethod]
    public void ReidentifiedParentCutRequiresCatalogAndRetainsFullEvidenceInsteadOfCandidateSubsets()
    {
        using var fixture = new Fixture(highlightedCut: true);
        string transcriptBefore = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);
        fixture.Edit(0, new { kind = "text", text = "再生成されたショートにも訂正を保持する。" });
        LayoutSubtitleBridgeSnapshot head = fixture.Edit(0, Timing(Range(3, 4, 2)));
        SubtitleEditCapture capture = fixture.Capture();
        SourceSpan targetSpan = Reidentify(fixture.CutSpan);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(cutSpan: targetSpan);
        Assert.AreNotEqual(fixture.CutSpan.Id, targetSpan.Id);
        Assert.IsFalse(capture.Head.Subtitles.Any(binding => binding.History.SourceCandidate.SourceSpan.Id == fixture.CutSpan.Id));
        Assert.IsTrue(fixture.CutSpan.TranscriptTokenIds.Count > capture.Head.Subtitles[0].History.SourceCandidate.EvidenceTokenIds.Count);
        AssertFailure(SubtitleEditProjectionFailureReason.MissingCue, capture, target);
        SourceSpan[] sourceSpans = [fixture.CutSpan];
        SourceSpan[] targetSpans = [targetSpan];
        SubtitleProjectionCutCatalog catalog = SubtitleProjectionCutCatalog.Create(sourceSpans, targetSpans);
        sourceSpans[0] = Reidentify(fixture.CutSpan);
        targetSpans[0] = Reidentify(targetSpan);

        SubtitleEditProjectionResult result = Apply(capture, target, catalog);

        SubtitleCueHistory actual = result.Output.Histories[0];
        Assert.AreEqual("再生成されたショートにも訂正を保持する。", actual.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(head.Subtitles[0].History.CurrentRevision.Snapshot.Cue.Range, actual.CurrentRevision.Snapshot.Cue.Range);
        Assert.AreEqual(head.Subtitles[0].History.CurrentRevision.Snapshot.TextOrigin, actual.CurrentRevision.Snapshot.TextOrigin);
        Assert.AreEqual(target.Histories[0].SourceCandidate.Origin, actual.CurrentRevision.SourceOrigin);
        Assert.AreEqual(0, result.Evidence.NewHumanActionCount);
        Assert.AreSame(catalog, result.Evidence.CutCatalog);
        Assert.AreEqual(fixture.CutSpan.Id, catalog.SourceSpans.Single().Id);
        Assert.AreEqual(targetSpan.Id, catalog.TargetSpans.Single().Id);
        Assert.AreEqual(targetSpan.Id, result.Evidence.GetTargetCutSpan(result.Output.Histories[1].CueId)!.Id,
            "An unedited sibling retains the actual parent span rather than a candidate subset.");
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SourceSpan>)catalog.SourceSpans).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SourceSpan>)catalog.TargetSpans).Clear());
        Assert.IsEmpty(typeof(SubtitleProjectionCutCatalog).GetConstructors());
        using JsonDocument proof = JsonDocument.Parse(result.Evidence.CanonicalJson);
        JsonElement cue = proof.RootElement.GetProperty("cues")[0];
        Assert.AreEqual(catalog.ContentDigest.Hex, proof.RootElement.GetProperty("cutCatalog").GetProperty("digest").GetString());
        Assert.AreEqual(fixture.CutSpan.Id.Value, cue.GetProperty("SourceCutSpan").GetProperty("Id").GetProperty("Value").GetGuid());
        Assert.AreEqual(targetSpan.Id.Value, cue.GetProperty("TargetCutSpan").GetProperty("Id").GetProperty("Value").GetGuid());
        Assert.AreEqual(cue.GetProperty("sourceCutIdentityDigest").GetString(), cue.GetProperty("targetCutIdentityDigest").GetString());
        Assert.AreEqual(result.Evidence.CanonicalJson, Apply(capture, target,
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [targetSpan])).Evidence.CanonicalJson);
        Assert.AreEqual(transcriptBefore, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        result.Evidence.ValidateSnapshot(fixture.Snapshot(result.Output));
    }

    [TestMethod]
    [DataRow("asset", false)]
    [DataRow("asset", true)]
    [DataRow("digest", false)]
    [DataRow("digest", true)]
    [DataRow("stream", false)]
    [DataRow("stream", true)]
    [DataRow("transcript", false)]
    [DataRow("transcript", true)]
    [DataRow("tokens", false)]
    [DataRow("tokens", true)]
    [DataRow("tokenOrder", false)]
    [DataRow("tokenOrder", true)]
    [DataRow("range", false)]
    [DataRow("range", true)]
    [DataRow("id", false)]
    [DataRow("id", true)]
    public void CatalogCannotBindMatchingPositionsToDifferentActualSourceEvidence(string field, bool changeSource)
    {
        using var fixture = new Fixture(highlightedCut: true);
        fixture.Edit(0, new { kind = "text", text = "ID や時刻だけでは根拠にしない。" });
        SourceSpan targetSpan = Reidentify(fixture.CutSpan);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(cutSpan: targetSpan);
        SourceSpan original = changeSource ? fixture.CutSpan : targetSpan;
        var changed = new SourceSpan(field == "id" ? EntityId<SourceSpanIdTag>.New() : original.Id,
            field == "asset" ? EntityId<AssetIdTag>.New() : original.AssetId,
            field == "stream" ? "audio:99" : original.StreamId,
            field == "range" ? Range(1, 9) : original.Range,
            field == "digest" ? Hash("different actual source bytes") : original.SourceDigest,
            field == "transcript" ? EntityId<TranscriptRevisionIdTag>.New() : original.TranscriptRevisionId,
            field == "tokenOrder" ? original.TranscriptTokenIds.Reverse().ToArray()
                : field == "tokens" ? original.TranscriptTokenIds.Select(_ => EntityId<TranscriptTokenIdTag>.New()).ToArray()
                : original.TranscriptTokenIds);
        SubtitleProjectionCutCatalog valid = SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [targetSpan]);
        SubtitleProjectionCutCatalog catalog = SubtitleProjectionCutCatalog.Create(
            [changeSource ? changed : fixture.CutSpan], [changeSource ? targetSpan : changed]);
        Assert.AreNotEqual(valid.ContentDigest, catalog.ContentDigest);
        AssertFailure(SubtitleEditProjectionFailureReason.EvidenceMismatch, fixture.Capture(), target, catalog);
        Assert.HasCount(1, fixture.Rows);
    }

    [TestMethod]
    public void CatalogRequiresWholeParentCorrespondenceEvenWhenEveryCandidateStillFits()
    {
        using var fixture = new Fixture(highlightedCut: true);
        fixture.Edit(0, new { kind = "text", text = "親区間の完全な根拠を比較する。" });
        SourceSpan targetSpan = Reidentify(fixture.CutSpan);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(cutSpan: targetSpan);
        foreach (SourceSpan changed in new[]
        {
            new SourceSpan(targetSpan.Id, targetSpan.AssetId, targetSpan.StreamId, Range(0, 11), targetSpan.SourceDigest,
                targetSpan.TranscriptRevisionId, targetSpan.TranscriptTokenIds),
            new SourceSpan(targetSpan.Id, targetSpan.AssetId, targetSpan.StreamId, targetSpan.Range, targetSpan.SourceDigest,
                targetSpan.TranscriptRevisionId, targetSpan.TranscriptTokenIds.Append(EntityId<TranscriptTokenIdTag>.New()).ToArray()),
        })
        {
            AssertFailure(SubtitleEditProjectionFailureReason.MissingCue, fixture.Capture(), target,
                SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [changed]));
        }
        // Both the real parent span AND the selected cut range must correspond for
        // different origins. Legacy same-ID cut-offset tests remain separate.
        AssertFailure(SubtitleEditProjectionFailureReason.MissingCue, fixture.Capture(), fixture.Generate(cut: Range(1, 9), cutSpan: targetSpan),
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [targetSpan]));
    }

    [TestMethod]
    public void CatalogRejectsMissingUnusedDuplicateAndCandidateOnlySpanIdsWithBoundedCounts()
    {
        using var fixture = new Fixture(highlightedCut: true);
        fixture.Edit(0, new { kind = "text", text = "完全な親 span が必要。" });
        SubtitleEditCapture capture = fixture.Capture();
        SourceSpan targetSpan = Reidentify(fixture.CutSpan);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(cutSpan: targetSpan);
        foreach (SubtitleProjectionCutCatalog catalog in new[]
        {
            SubtitleProjectionCutCatalog.Create([], [targetSpan]),
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan], []),
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan, Reidentify(fixture.CutSpan)], [targetSpan]),
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [targetSpan, Reidentify(targetSpan)]),
            SubtitleProjectionCutCatalog.Create([capture.Head.Subtitles[0].History.SourceCandidate.SourceSpan], [targetSpan]),
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [target.Histories[0].SourceCandidate.SourceSpan]),
        })
            AssertFailure(SubtitleEditProjectionFailureReason.EvidenceMismatch, capture, target, catalog);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.EvidenceMismatch, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan, fixture.CutSpan], [targetSpan])).Reason);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.EvidenceMismatch, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [targetSpan, targetSpan])).Reason);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.EvidenceMismatch, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleProjectionCutCatalog.Create([null!], [targetSpan])).Reason);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.ResourceLimit, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleProjectionCutCatalog.Create(Enumerable.Repeat(fixture.CutSpan, StandardRenderGraphFactory.MaximumCuts + 1).ToArray(), [targetSpan])).Reason);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReidentifiedTimingKeepsOriginalEvidenceAcrossStrictHopAndLaterReidentification(bool replaceTiming)
    {
        using var fixture = new Fixture(highlightedCut: true);
        fixture.Edit(0, new { kind = "text", text = "繰り返し投影でも訂正を維持する。" });
        fixture.Edit(0, Timing(Range(3, 4, 2)));
        SourceSpan secondSpan = Reidentify(fixture.CutSpan);
        SubtitleEditProjectionResult first = Apply(fixture.Capture(), fixture.Generate(cutSpan: secondSpan),
            SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [secondSpan]));
        LayoutSubtitleBridgeSnapshot firstRoot = fixture.RegisterProjection(first);
        SubtitleEditProjection.ProjectedField originalTiming = first.Evidence.GetInheritedFields(first.Output.Histories[0].CueId)
            .Single(field => field.Revision.Change.Kind == SubtitleCueChangeKind.Timing);
        // No catalog on the middle hop: only the exact existing origin may match.
        SubtitleEditProjectionResult second = Apply(fixture.Capture(firstRoot.CompositionVersion.Id), fixture.Generate(cutSpan: secondSpan));
        Assert.IsNull(second.Evidence.CutCatalog);
        LayoutSubtitleBridgeSnapshot secondRoot = fixture.RegisterProjection(second);
        if (replaceTiming) { fixture.Edit(secondRoot.CompositionVersion.Id, 0, Timing(Range(2, 2))); }
        SubtitleEditCapture captured = fixture.Capture(secondRoot.CompositionVersion.Id);
        SourceSpan thirdSpan = Reidentify(secondSpan);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(cutSpan: thirdSpan);
        AssertFailure(SubtitleEditProjectionFailureReason.MissingCue, captured, target);
        SubtitleEditProjectionResult third = Apply(captured, target, SubtitleProjectionCutCatalog.Create([secondSpan], [thirdSpan]));
        SubtitleEditProjection.ProjectedField timing = third.Evidence.GetInheritedFields(third.Output.Histories[0].CueId)
            .Single(field => field.Revision.Change.Kind == SubtitleCueChangeKind.Timing);

        Assert.AreEqual(replaceTiming ? Range(2, 2) : Range(3, 4, 2), third.Output.Histories[0].CurrentRevision.Snapshot.Cue.Range);
        Assert.AreEqual(first.Output.Histories[0].CurrentRevision.Snapshot.Text, third.Output.Histories[0].CurrentRevision.Snapshot.Text);
        Assert.AreEqual(replaceTiming ? secondSpan.Id : fixture.CutSpan.Id, timing.SourceCutSpan!.Id);
        Assert.AreEqual(replaceTiming ? captured.Edits.Single().RecordDigest : originalTiming.Record.RecordDigest, timing.Record.RecordDigest);
        Assert.AreEqual(0, third.Evidence.NewHumanActionCount);
        Assert.AreEqual(3, third.Evidence.ProjectionDepth);
        third.Evidence.ValidateSnapshot(fixture.Snapshot(third.Output));
    }

    [TestMethod]
    public void FirstCatalogCanBindAnOriginalTimingFieldFromAnEarlierStrictIdProjection()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, Timing(Range(3, 4, 2)));
        SubtitleEditProjectionResult first = Apply(fixture.Capture(), fixture.Generate());
        Assert.IsNull(first.Evidence.GetInheritedFields(first.Output.Histories[0].CueId).Single().SourceCutSpan);
        LayoutSubtitleBridgeSnapshot root = fixture.RegisterProjection(first);
        SourceSpan targetSpan = Reidentify(fixture.Span);
        SubtitleEditProjectionResult second = Apply(fixture.Capture(root.CompositionVersion.Id), fixture.Generate(cutSpan: targetSpan),
            SubtitleProjectionCutCatalog.Create([fixture.Span], [targetSpan]));
        Assert.AreEqual(fixture.Span.Id, second.Evidence.GetInheritedFields(second.Output.Histories[0].CueId).Single().SourceCutSpan!.Id);
        root = fixture.RegisterProjection(second);
        SubtitleEditProjectionResult third = Apply(fixture.Capture(root.CompositionVersion.Id), fixture.Generate(cutSpan: targetSpan));
        Assert.AreEqual(Range(3, 4, 2), third.Output.Histories[0].CurrentRevision.Snapshot.Cue.Range);
    }

    [TestMethod]
    public void ReidentifiedRepeatedCutsRequireUnambiguousFullOccurrencesAndEqualMultiplicity()
    {
        using var fixture = new Fixture(repeated: true, highlightedCut: true);
        fixture.Edit(2, new { kind = "text", text = "二度目だけ再 ID 化しても維持する。" });
        fixture.Edit(2, Timing(Range(23, 4, 2)));
        SourceSpan targetSpan = Reidentify(fixture.CutSpan);
        SubtitleProjectionCutCatalog catalog = SubtitleProjectionCutCatalog.Create([fixture.CutSpan], [targetSpan]);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(repeated: true, cutSpan: targetSpan);
        SubtitleEditProjectionResult result = Apply(fixture.Capture(), target, catalog);
        Assert.AreSame(target.Histories[0], result.Output.Histories[0]);
        Assert.AreEqual("二度目だけ再 ID 化しても維持する。", result.Output.Histories[2].CurrentRevision.Snapshot.Text);
        Assert.AreEqual(Range(23, 4, 2), result.Output.Histories[2].CurrentRevision.Snapshot.Cue.Range);
        AssertFailure(SubtitleEditProjectionFailureReason.AmbiguousCue, fixture.Capture(), fixture.Generate(cutSpan: targetSpan), catalog);
        AssertFailure(SubtitleEditProjectionFailureReason.AmbiguousCue, fixture.Capture(),
            fixture.Generate(repeated: true, cut: Range(1, 9), cutSpan: targetSpan), catalog);
    }

    [TestMethod]
    public void ValidatedOutputAccessorReturnsHeldImmutableOutputAndCannotMintOrReplaceEvidence()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "保持済み出力だけを返す。" });
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate();
        SubtitleEditProjectionResult result = Apply(fixture.Capture(), target);
        SourceOnlyGeneratedSubtitleRuntimeEvidence held = result.Evidence.GetValidatedOutput();
        Assert.AreSame(held, result.Evidence.GetValidatedOutput());
        Assert.AreEqual(result.Evidence.OutputDigest, SubtitleEditProjection.GeneratedDigest(held));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SubtitleCueHistory>)held.Histories).Clear());
        SourceOnlyGeneratedSubtitleRuntimeEvidence replaced = held with { Histories = target.Histories };
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => result.Evidence.ValidateOutput(replaced));
        Assert.AreEqual("保持済み出力だけを返す。", result.Evidence.GetValidatedOutput().Histories[0].CurrentRevision.Snapshot.Text);
    }

    [TestMethod]
    [DataRow("head")]
    [DataRow("initial")]
    [DataRow("generated")]
    [DataRow("record")]
    [DataRow("commit")]
    [DataRow("previous")]
    [DataRow("sequence")]
    [DataRow("parent")]
    [DataRow("root")]
    [DataRow("result")]
    public void WithClonedCaptureOrJournalCannotBypassDigestAndPrefixValidation(string field)
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "保存済み編集。" });
        SubtitleEditCapture capture = fixture.Capture();
        StoredSubtitleEdit row = capture.Edits[0];
        ContentDigest wrong = Hash("not the captured evidence");
        capture = field switch
        {
            "head" => capture with { HeadSnapshotDigest = wrong },
            "initial" => capture with { InitialSnapshotDigest = wrong },
            "generated" => capture with { GeneratedInputDigest = wrong },
            "record" => capture with { Edits = [row with { RecordDigest = wrong }] },
            "commit" => capture with { Edits = [row with { CommitDigest = wrong }] },
            "previous" => capture with { Edits = [row with { PreviousRecordDigest = wrong }] },
            "sequence" => capture with { Edits = [row with { Sequence = 2 }] },
            "parent" => capture with { Edits = [row with { ParentVersionId = row.ChildVersionId }] },
            "root" => capture with { RootVersionId = row.ChildVersionId },
            "result" => capture with { Edits = [row with { ResultSnapshotDigest = wrong }] },
            _ => throw new AssertFailedException(),
        };
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidCapture, capture, fixture.Generate());
        Assert.HasCount(1, fixture.Rows);
    }

    [TestMethod]
    public void RehashedButUnexecutedOperationStillFailsNativeReplay()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "実際の編集。" });
        SubtitleEditCapture capture = fixture.Capture();
        StoredSubtitleEdit row = capture.Edits.Single();
        JsonNode commit = JsonNode.Parse(row.CommitCanonicalJson)!;
        commit["operation"]!["edit"]!["text"] = "実行していない偽の編集。";
        string canonical = CanonicalJson.Canonicalize(commit.ToJsonString());
        ContentDigest digest = Hash(canonical);
        row = row with
        {
            OperationCanonicalJson = CanonicalJson.Canonicalize(commit["operation"]!.ToJsonString()),
            CommitCanonicalJson = canonical,
            CommitDigest = digest,
            RecordDigest = Hash(CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
            {
                schema = "subtitle-edit.record.v1",
                sequence = row.Sequence,
                commitDigest = digest.Hex,
                rootPayloadDigest = row.RootPayloadDigest.Hex,
                childPayloadDigest = row.ChildPayloadDigest.Hex,
                previousRecordDigest = row.PreviousRecordDigest?.Hex,
            }))),
        };
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidCapture, capture with { Edits = [row] }, fixture.Generate());
    }

    [TestMethod]
    public void SavedPrefixRemainsDeterministicAfterLaterEditAndServiceRestart()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "捕捉した時点。" });
        SubtitleEditCapture capture = fixture.Capture();
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate();
        SubtitleEditProjectionResult first = Apply(capture, target);
        var restarted = new DurableSubtitleEditService(new SubtitleEditStore(fixture.DatabasePath));
        restarted.RegisterGenerated(fixture.Initial, fixture.GeneratedInputDigest);
        SubtitleEditCapture restored = restarted.Capture(fixture.Initial.ProjectId, [fixture.Root.Version.Id]).Single();
        fixture.Edit(0, new { kind = "text", text = "捕捉後の編集。" });
        SubtitleEditProjectionResult replay = Apply(restored, target);
        Assert.AreEqual(first.Evidence.CanonicalJson, replay.Evidence.CanonicalJson);
        Assert.AreEqual(first.Evidence.ContentDigest, replay.Evidence.ContentDigest);
        Assert.AreEqual(SubtitleEditProjection.GeneratedDigest(first.Output), SubtitleEditProjection.GeneratedDigest(replay.Output));
        Assert.AreEqual("捕捉した時点。", replay.Output.Histories[0].CurrentRevision.Snapshot.Text);
        Assert.HasCount(2, fixture.Rows);
    }

    [TestMethod]
    public void RepeatedProjectionWithoutNewEditsPreservesEveryFieldOriginalClockAndEditorialProvenance()
    {
        using var fixture = new Fixture();
        string transcript = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);
        fixture.Edit(0, new { kind = "text", text = "再生成を繰り返しても保持する字幕。" });
        fixture.Edit(0, new { kind = "style", style = "quote" });
        fixture.Edit(0, Timing(Range(3, 4, 2)));
        LayoutSubtitleBridgeSnapshot edited = fixture.Edit(0, fixture.Placement());
        SubtitleEditCapture original = fixture.Capture();
        SubtitleCueSnapshot manual = edited.Subtitles[0].History.CurrentRevision.Snapshot;
        SubtitleEditProjectionResult result = Apply(original, fixture.Generate(cut: Range(1, 9)));
        IReadOnlyList<SubtitleEditProjection.ProjectedField> originalFields = result.Evidence.GetInheritedFields(result.Output.Histories[0].CueId);
        Assert.HasCount(4, originalFields);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SubtitleEditProjection.ProjectedField>)originalFields).Clear());

        for (int depth = 2; depth <= 3; depth++)
        {
            LayoutSubtitleBridgeSnapshot registered = fixture.RegisterProjection(result);
            var restarted = new DurableSubtitleEditService(new SubtitleEditStore(fixture.DatabasePath));
            restarted.RegisterGenerated(registered, SubtitleEditProjection.GeneratedDigest(result.Output), projection: result.Evidence);
            SubtitleEditCapture capture = restarted.CapturePrefix(registered.ProjectId, registered.CompositionVersion.Id, 0, null);
            Assert.AreSame(result.Evidence, capture.InitialProjection);
            Assert.AreEqual(0, capture.Sequence);
            Assert.HasCount(1, capture.Head.Subtitles[0].History.Revisions);
            SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate(cut: depth == 2 ? Range(0, 10) : Range(1, 9));
            ContentDigest targetBefore = SubtitleEditProjection.GeneratedDigest(target);
            SubtitleEditProjectionEvidence prior = result.Evidence;
            result = Apply(capture, target);

            SubtitleCueHistory history = result.Output.Histories[0];
            SubtitleCueSnapshot actual = history.CurrentRevision.Snapshot;
            Assert.AreEqual(depth, result.Evidence.ProjectionDepth);
            Assert.AreEqual(manual.Text, actual.Text);
            Assert.AreEqual(manual.Style, actual.Style);
            Assert.AreEqual(manual.TextOrigin, actual.TextOrigin);
            Assert.AreEqual(manual.TextOrigin, history.CurrentRevision.Change.Origin);
            Assert.AreEqual(depth == 2 ? Range(3, 4, 2) : Range(1, 4, 2), actual.Cue.Range);
            Assert.AreEqual(manual.ResolvedPlacement!.Rect, actual.ResolvedPlacement!.Rect);
            Assert.AreEqual(history.SourceCandidate.Origin, history.CurrentRevision.SourceOrigin);
            Assert.AreEqual(original.Head.Subtitles[0].History.SourceCandidate.Text, history.SourceCandidate.Text);
            Assert.AreEqual(SubtitleEditProjection.SourceIdentityDigest(original.Head.Subtitles[0].History.SourceCandidate),
                SubtitleEditProjection.SourceIdentityDigest(history.SourceCandidate));
            Assert.HasCount(1, history.Revisions);
            Assert.AreEqual(0, result.Evidence.NewHumanActionCount);
            CollectionAssert.AreEqual(prior.TrustedEditorialRecordIds.ToArray(), result.Evidence.TrustedEditorialRecordIds.ToArray());
            IReadOnlyList<SubtitleEditProjection.ProjectedField> fields = result.Evidence.GetInheritedFields(history.CueId);
            CollectionAssert.AreEqual(originalFields.Select(field => field.Record.RecordDigest).ToArray(), fields.Select(field => field.Record.RecordDigest).ToArray());
            Assert.IsTrue(fields.All(field => field.Record.RootVersionId == fixture.Root.Version.Id));
            Assert.AreSame(target.Histories[1], result.Output.Histories[1]);
            Assert.AreEqual(targetBefore, SubtitleEditProjection.GeneratedDigest(target));
            using JsonDocument canonical = JsonDocument.Parse(result.Evidence.CanonicalJson);
            Assert.AreEqual(prior.ContentDigest.Hex, canonical.RootElement.GetProperty("priorProofDigest").GetString());
            Assert.AreEqual(prior.CanonicalJson, canonical.RootElement.GetProperty("priorProofCanonical").GetRawText());
            Assert.AreEqual(0, canonical.RootElement.GetProperty("source").GetProperty("recordPrefix").GetArrayLength());
            foreach (JsonElement field in canonical.RootElement.GetProperty("cues")[0].GetProperty("fields").EnumerateArray())
            {
                Assert.AreEqual(fixture.Root.Version.Id.Value, field.GetProperty("sourceRootVersionId").GetProperty("Value").GetGuid());
                Assert.AreEqual(Editor, field.GetProperty("sourceActorSid").GetString());
            }
        }
        Assert.AreEqual(transcript, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        Assert.HasCount(4, fixture.Rows);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("style")]
    [DataRow("timing")]
    public void NewFieldOnProjectedRootOverridesOnlyThatFieldAndKeepsOtherOriginalRecords(string kind)
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "最初の字幕編集。" });
        fixture.Edit(0, new { kind = "style", style = "quote" });
        fixture.Edit(0, Timing(Range(3, 4, 2)));
        SubtitleEditProjectionResult first = Apply(fixture.Capture(), fixture.Generate(cut: Range(1, 9)));
        LayoutSubtitleBridgeSnapshot root = fixture.RegisterProjection(first);
        object edit = kind switch
        {
            "text" => new { kind, text = "投影後の新しい字幕編集。" },
            "style" => new { kind, style = "emphasis" },
            "timing" => Timing(Range(1, 3)),
            _ => throw new AssertFailedException(),
        };
        LayoutSubtitleBridgeSnapshot changed = fixture.Edit(root.CompositionVersion.Id, 0, edit);
        SubtitleCueRevision revision = changed.Subtitles[0].History.CurrentRevision;
        SubtitleEditCapture accepted = fixture.Capture(root.CompositionVersion.Id);
        Assert.AreEqual(1, accepted.Sequence);
        Assert.AreSame(first.Evidence, accepted.InitialProjection);
        fixture.Edit(root.CompositionVersion.Id, 1, new { kind = "style", style = "caution" });
        SubtitleEditCapture prefix = fixture.CapturePrefix(root.CompositionVersion.Id, 1, accepted.Edits.Single().RecordDigest);
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate();
        SubtitleEditProjectionResult second = Apply(prefix, target);

        SubtitleCueHistory history = second.Output.Histories[0];
        Assert.AreEqual(kind == "text" ? revision.Snapshot.Text : first.Output.Histories[0].CurrentRevision.Snapshot.Text, history.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(kind == "style" ? SubtitleCueStyle.Emphasis : SubtitleCueStyle.Quote, history.CurrentRevision.Snapshot.Style);
        Assert.AreEqual(kind == "timing" ? Range(2, 3) : Range(3, 4, 2), history.CurrentRevision.Snapshot.Cue.Range);
        Assert.AreEqual(kind == "text" ? revision.Change.Origin : first.Output.Histories[0].CurrentRevision.Snapshot.TextOrigin,
            history.CurrentRevision.Snapshot.TextOrigin);
        Assert.AreSame(target.Histories[1], second.Output.Histories[1], "An edit after the accepted prefix must not leak into regeneration.");
        var before = first.Evidence.GetInheritedFields(first.Output.Histories[0].CueId).ToDictionary(field => field.Revision.Change.Kind);
        IReadOnlyList<SubtitleEditProjection.ProjectedField> fields = second.Evidence.GetInheritedFields(history.CueId);
        Assert.HasCount(3, fields);
        foreach (SubtitleEditProjection.ProjectedField field in fields)
        {
            if (field.Revision.Change.Kind == revision.Change.Kind)
            {
                Assert.AreEqual(revision.Id, field.Revision.Id);
                Assert.AreEqual(root.CompositionVersion.Id, field.Record.RootVersionId);
                Assert.AreEqual(accepted.Edits.Single().RecordDigest, field.Record.RecordDigest);
            }
            else
            {
                Assert.AreEqual(before[field.Revision.Change.Kind].Revision.Id, field.Revision.Id);
                Assert.AreEqual(fixture.Root.Version.Id, field.Record.RootVersionId);
                Assert.AreEqual(before[field.Revision.Change.Kind].Record.RecordDigest, field.Record.RecordDigest);
            }
        }
        Assert.IsFalse(second.Evidence.TrustedEditorialRecordIds.Contains(before[revision.Change.Kind].Revision.Change.Origin.EditorialRecordId!));
        Assert.IsTrue(second.Evidence.TrustedEditorialRecordIds.Contains(revision.Change.Origin.EditorialRecordId!));
        LayoutSubtitleBridgeSnapshot secondRoot = fixture.RegisterProjection(second);
        SubtitleEditProjectionResult third = Apply(fixture.Capture(secondRoot.CompositionVersion.Id), fixture.Generate());
        Assert.AreEqual(history.CurrentRevision.Snapshot.Text, third.Output.Histories[0].CurrentRevision.Snapshot.Text);
        Assert.AreEqual(history.CurrentRevision.Snapshot.Style, third.Output.Histories[0].CurrentRevision.Snapshot.Style);
        Assert.AreEqual(history.CurrentRevision.Snapshot.Cue.Range, third.Output.Histories[0].CurrentRevision.Snapshot.Cue.Range);
        CollectionAssert.AreEqual(second.Evidence.TrustedEditorialRecordIds.ToArray(), third.Evidence.TrustedEditorialRecordIds.ToArray());
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("timing")]
    [DataRow("revertedStyle")]
    public void ProjectedRootRejectsMissingWrongProofAndWrongInitialDigestWithoutTreatingItAsFreshGeneration(string kind)
    {
        using var fixture = new Fixture();
        if (kind == "revertedStyle")
        {
            fixture.Edit(0, new { kind = "style", style = "emphasis" });
            fixture.Edit(0, new { kind = "style", style = "normal" });
        }
        else
        {
            fixture.Edit(0, kind == "timing" ? Timing(Range(3, 4, 2)) : new { kind, text = "正しい証明が必要な字幕。" });
        }
        SubtitleEditCapture original = fixture.Capture();
        SourceOnlyGeneratedSubtitleRuntimeEvidence generated = fixture.Generate();
        SubtitleEditProjectionResult first = Apply(original, generated);
        LayoutSubtitleBridgeSnapshot root = fixture.RegisterProjection(first);
        SubtitleEditCapture capture = fixture.Capture(root.CompositionVersion.Id);
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidCapture, capture with { InitialProjection = null }, fixture.Generate());
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidCapture, capture with { InitialSnapshotDigest = Hash("wrong projected initial") }, fixture.Generate());
        SubtitleEditProjectionResult unrelated = Apply(original, fixture.Generate());
        AssertFailure(SubtitleEditProjectionFailureReason.EvidenceMismatch, capture with { InitialProjection = unrelated.Evidence }, fixture.Generate());
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidTarget, original, first.Output);
        var service = new DurableSubtitleEditService(new SubtitleEditStore(fixture.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.RegisterGenerated(root, SubtitleEditProjection.GeneratedDigest(first.Output)));
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => service.RegisterGenerated(root, SubtitleEditProjection.GeneratedDigest(first.Output), projection: unrelated.Evidence));
        service.RegisterGenerated(root, SubtitleEditProjection.GeneratedDigest(first.Output), projection: first.Evidence);
        Assert.AreEqual(0, service.CapturePrefix(root.ProjectId, root.CompositionVersion.Id, 0, null).Sequence);
    }

    [TestMethod]
    public void ReturnedFieldCopiesCannotReplaceNativeEvidenceOrBorrowAnotherBranchesRecord()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "元の根拠を保持する。" });
        SubtitleEditProjectionResult first = Apply(fixture.Capture(), fixture.Generate());
        LayoutSubtitleBridgeSnapshot root = fixture.RegisterProjection(first);
        EntityId<CueIdTag> cueId = first.Output.Histories[0].CueId;
        SubtitleEditProjection.ProjectedField original = first.Evidence.GetInheritedFields(cueId).Single();
        SubtitleEditProjection.ProjectedField[] copies = first.Evidence.GetInheritedFields(cueId).ToArray();
        copies[0] = original with { Record = original.Record with { RootVersionId = root.CompositionVersion.Id, RecordDigest = Hash("forged returned copy") } };
        Assert.AreEqual(original.Record.RecordDigest, first.Evidence.GetInheritedFields(cueId).Single().Record.RecordDigest);
        Assert.AreEqual(original.Record.RootVersionId, first.Evidence.GetInheritedFields(cueId).Single().Record.RootVersionId);
        Assert.IsEmpty(first.Evidence.GetInheritedFields(first.Output.Histories[1].CueId));
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => first.Evidence.GetInheritedFields(EntityId<CueIdTag>.New()));
        SubtitleEditProjectionResult second = Apply(fixture.Capture(root.CompositionVersion.Id), fixture.Generate());
        Assert.AreEqual(original.Record.RecordDigest, second.Evidence.GetInheritedFields(second.Output.Histories[0].CueId).Single().Record.RecordDigest);
        Assert.AreEqual(original.Revision.Change.Origin, second.Output.Histories[0].CurrentRevision.Snapshot.TextOrigin);
    }

    [TestMethod]
    public void ProjectionProofBindsFullHistoriesLayoutProfileAndCheckpointSpecificNativeContext()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "レイアウト文脈も検証する。" });
        SourceOnlyGeneratedSubtitleRuntimeEvidence generated = fixture.Generate();
        SubtitleEditProjectionResult result = Apply(fixture.Capture(), generated);
        LayoutSubtitleBridgeSnapshot snapshot = fixture.Snapshot(result.Output);
        result.Evidence.ValidateSnapshot(snapshot);
        result.Evidence.ValidateSnapshot(snapshot, fixture.Snapshot);
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => result.Evidence.ValidateSnapshot(fixture.Snapshot(generated)));
        LayoutSubtitleCueBinding binding = snapshot.Subtitles[0];
        var changedBinding = new LayoutSubtitleCueBinding(binding.History, binding.LayoutSpec, binding.LayoutProfile,
            [new SubtitleProhibitedRegion("unexpected-native-region", snapshot.OutputEnvelope.SafeZones[0].Rect)], []);
        var changed = new LayoutSubtitleBridgeSnapshot(snapshot.ProjectId, 0, snapshot.CompositionVersion, snapshot.OutputEnvelope,
            snapshot.OutputPixelWidth, snapshot.OutputPixelHeight, snapshot.Sources, snapshot.Templates,
            [changedBinding, snapshot.Subtitles[1]], [], snapshot.SubtitleDisplays);
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => result.Evidence.ValidateSnapshot(changed));
        var extraContext = new LayoutSubtitleBridgeSnapshot(snapshot.ProjectId, 0, snapshot.CompositionVersion, snapshot.OutputEnvelope,
            snapshot.OutputPixelWidth, snapshot.OutputPixelHeight, snapshot.Sources, snapshot.Templates, snapshot.Subtitles, [], snapshot.SubtitleDisplays,
            compositionVersions: new Dictionary<EntityId<CompositionVersionIdTag>, CompositionVersion> { [fixture.Root.Version.Id] = fixture.Root.Version });
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => result.Evidence.ValidateSnapshot(extraContext, fixture.Snapshot));
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(snapshot), DurableSubtitleEditService.ComputeInitialDigest(snapshot));
    }

    [TestMethod]
    public void NativeProjectionChainAllows128LevelsAndRejects129BeforeTraversingIt()
    {
        using var fixture = new Fixture();
        SubtitleEditCapture capture = fixture.Capture();
        for (int depth = 1; depth <= SubtitleEditProjection.MaximumProjectionDepth; depth++)
        {
            SubtitleEditProjectionResult result = Apply(capture, fixture.Generate());
            Assert.AreEqual(depth, result.Evidence.ProjectionDepth);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(result.Evidence.CanonicalJson) <= SubtitleEditProjection.MaximumCanonicalUtf8Bytes);
            LayoutSubtitleBridgeSnapshot snapshot = fixture.Snapshot(result.Output);
            ContentDigest digest = DurableSubtitleEditService.ComputeSnapshotDigest(snapshot);
            capture = new SubtitleEditCapture(snapshot.ProjectId, snapshot.CompositionVersion.Id,
                SubtitleEditProjection.GeneratedDigest(result.Output), digest, snapshot, digest, [])
            { InitialProjection = result.Evidence };
        }
        AssertFailure(SubtitleEditProjectionFailureReason.ResourceLimit, capture, fixture.Generate());
        Assert.IsEmpty(fixture.Rows);
    }

    [TestMethod]
    public void EvidenceBudgetsRejectOversizeCanonicalOutputAndInvalidDepth()
    {
        SubtitleEditProjection.ValidateEvidenceBudget(128, 32L * 1024 * 1024);
        foreach ((int depth, long bytes) in new[] { (0, 0L), (129, 0L), (1, -1L), (1, 32L * 1024 * 1024 + 1) })
            Assert.AreEqual(SubtitleEditProjectionFailureReason.ResourceLimit,
                Assert.ThrowsExactly<SubtitleEditProjectionException>(() => SubtitleEditProjection.ValidateEvidenceBudget(depth, bytes)).Reason);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.ResourceLimit, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleEditProjection.Canonical(new { value = new string('x', SubtitleEditProjection.MaximumCanonicalUtf8Bytes) })).Reason);
    }

    [TestMethod]
    public void MaterializedInitialRequiresExactRederivedProofAndPublicInitialRemainsSourceOnly()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "再適用した編集。" });
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate();
        SubtitleEditProjectionResult result = Apply(fixture.Capture(), target);
        foreach (SubtitleCueHistory history in result.Output.Histories)
            SubtitleService.ValidateMaterializedInitial(result.Output.Version, history, result.Evidence);
        SubtitleCueHistory projected = result.Output.Histories[0];
        SubtitleCueHistory direct = SubtitleService.CreateInitial(target.Version, projected.SourceCandidate,
            projected.CurrentRevision.Snapshot.LayoutResolution, projected.CueId, projected.CurrentRevision.Id,
            projected.CurrentRevision.Snapshot.Cue.Range, Coordinator, ProjectedAt, SubtitleEditProjection.MaterializationReason);
        Assert.AreEqual(projected.SourceCandidate.Text, direct.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(projected.SourceCandidate.Origin, direct.CurrentRevision.Snapshot.TextOrigin);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.EvidenceMismatch, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleService.ValidateMaterializedInitial(target.Version, direct, result.Evidence)).Reason);
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => result.Evidence.ValidateOutput(result.Output with { Histories = target.Histories }));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<EntityId<EditorialRecordIdTag>>)result.Evidence.TrustedEditorialRecordIds).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SubtitleCueHistory>)result.Output.Histories).Clear());
        Assert.IsEmpty(typeof(SubtitleEditProjectionEvidence).GetConstructors(), "No public constructor may accept self-asserted proof.");
        Assert.IsEmpty(typeof(SubtitleEditProjection.ValidatedProjection).GetConstructors());
    }

    [TestMethod]
    public void TargetBuildHistoriesTimelineAndCompositionCannotDisagree()
    {
        using var fixture = new Fixture();
        fixture.Edit(0, new { kind = "text", text = "対象版を検証する。" });
        SubtitleEditCapture capture = fixture.Capture();
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate();
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidTarget, capture, target with { Histories = [] });
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidTarget, capture, target with { Build = fixture.Root.Build });
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidTarget, capture, target with { Timeline = fixture.Root.Timeline });
        CompositionVersion version = target.Version;
        var corrupt = new CompositionVersion(version.Id, version.ProjectId, version.ParentVersionId, version.CreatedByActorId,
            version.CreatedAt, version.ChangeReason, Hash("wrong element digest"), version.Elements);
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidTarget, capture, target with { Version = corrupt });
    }

    [TestMethod]
    public void EmptyJournalDoesNotInventHumanEditsAndCanceledOrSameVersionInputsFailClosed()
    {
        using var fixture = new Fixture();
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = fixture.Generate();
        SubtitleEditCapture capture = fixture.Capture();
        SubtitleEditProjectionResult result = Apply(capture, target);
        Assert.AreEqual(0, result.Evidence.ProjectedCueCount);
        Assert.IsEmpty(result.Evidence.TrustedEditorialRecordIds);
        Assert.AreEqual(SubtitleEditProjection.GeneratedDigest(target), SubtitleEditProjection.GeneratedDigest(result.Output));
        AssertFailure(SubtitleEditProjectionFailureReason.InvalidTarget, capture, fixture.Root);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.InvalidActor, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleEditProjection.Apply(capture, target, "not-a-sid", ProjectedAt)).Reason);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.InvalidTimestamp, Assert.ThrowsExactly<SubtitleEditProjectionException>(() =>
            SubtitleEditProjection.Apply(capture, target, Coordinator, At)).Reason);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SubtitleEditProjection.Apply(capture, target, Coordinator, ProjectedAt, cancellation.Token));
        Assert.IsEmpty(fixture.Rows);
    }

    private static SubtitleEditProjectionResult Apply(SubtitleEditCapture source, SourceOnlyGeneratedSubtitleRuntimeEvidence target,
        SubtitleProjectionCutCatalog? catalog = null) =>
        SubtitleEditProjection.Apply(source, target, Coordinator, ProjectedAt, CancellationToken.None, catalog);

    private static void AssertFailure(SubtitleEditProjectionFailureReason reason, SubtitleEditCapture capture,
        SourceOnlyGeneratedSubtitleRuntimeEvidence target, SubtitleProjectionCutCatalog? catalog = null) =>
        Assert.AreEqual(reason, Assert.ThrowsExactly<SubtitleEditProjectionException>(() => Apply(capture, target, catalog)).Reason);

    private static ContentDigest Hash(string text) => ContentDigest.Compute(Encoding.UTF8.GetBytes(text));
    private static TimeRange Range(long start, long duration, long rate = 1) => new(new RationalTime(start, rate), new RationalTime(duration, rate));
    private static SourceSpan Reidentify(SourceSpan span) => new(EntityId<SourceSpanIdTag>.New(), span.AssetId, span.StreamId,
        span.Range, span.SourceDigest, span.TranscriptRevisionId, span.TranscriptTokenIds);
    private static object Timing(TimeRange range) => new
    {
        kind = "timing",
        outputRange = new
        {
            start = new { value = range.Start.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), rate = range.Start.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            duration = new { value = range.Duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), rate = range.Duration.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        },
    };
    private static object Rational(RationalNumber number) => new
    {
        numerator = number.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
        denominator = number.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "HubRadio.SubtitleEditProjection", Guid.NewGuid().ToString("N"));
        private readonly DefaultSettingsProfile settingsProfile;
        private readonly BundledRenderProfileDefinition reference;
        private readonly BundledRenderProfileDefinition render;
        private readonly EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        private readonly SubtitleEditStore store;
        private readonly DurableSubtitleEditService service;

        internal Fixture(bool repeated = false, bool splitCues = false, bool highlightedCut = false)
        {
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "store.db");
            Assert.IsTrue(DatabaseMigrator.Migrate(DatabasePath, Path.Combine(directory, "backups")).SchemaVersion >= 12);
            Projects = new ProjectStore(DatabasePath);
            settingsProfile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            Settings = settingsProfile.CreateSnapshot();
            if (splitCues) { Settings = WithMaximumDuration(1); }
            reference = BundledRenderProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")));
            render = StandardRenderProfileFactory.CreateLandscape(settingsProfile, Settings, reference, new RationalNumber(30, 1));
            Token[] tokens =
            [
                new(EntityId<TranscriptTokenIdTag>.New(), "原文", Range(1, 1), null),
                new(EntityId<TranscriptTokenIdTag>.New(), "一。", Range(21, 9, 10), null),
                new(EntityId<TranscriptTokenIdTag>.New(), "原文", Range(6, 1), null),
                new(EntityId<TranscriptTokenIdTag>.New(), "二。", Range(71, 9, 10), null),
            ];
            EntityId<TranscriptRevisionIdTag> revisionId = EntityId<TranscriptRevisionIdTag>.New();
            EntityId<SourceSpanIdTag> spanId = EntityId<SourceSpanIdTag>.New();
            const string text = "原文一。 原文二。";
            var utterance = new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), spanId, EntityId<TranscriptSpeakerIdTag>.New(),
                "Synthetic speaker", text, Range(0, 10), null, [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), text, tokens)]);
            Transcript = new TranscriptRevision(revisionId, null, TranscriptChangeSource.Machine, At, [utterance]);
            Span = new SourceSpan(spanId, EntityId<AssetIdTag>.New(), "audio:1", Range(0, 10), Hash("synthetic source only"), revisionId,
                tokens.Select(token => token.Id).ToArray());
            CutSpan = highlightedCut ? Reidentify(Span) : Span;
            var asset = new Asset(Span.AssetId, "synthetic.mov", "video/quicktime", Span.SourceDigest, 100,
                new RightsRecord(RightsStatus.Unconfirmed, null, "Synthetic fixture, not permission or approval", false));
            var project = new Project(projectId, "Projection fixture", "Native replay", "Editors", PublicationScope.InternalOnly, null, [asset], []);
            Projects.CreateProject(project, HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project), At);
            Root = Build(repeated, Range(0, 10), render.Layout, Transcript, Span, At.AddMinutes(1), cutSpan: CutSpan);
            Assert.HasCount((repeated ? 2 : 1) * (splitCues ? 4 : 2), Root.Histories);
            Projects.AppendCompositionVersion(Root.Version, CompositionSnapshotJson.WriteVersion(Root.Version));
            var profile = new SubtitleLayoutProfile(Root.Layout.Id, Root.Build.Subtitles.DisplayProfile.VersionDigest,
                Root.Layout.SafeZones.Select(zone => zone.Name).ToArray(),
                Enum.GetValues<SubtitleSoftConstraintKind>().Select(kind => new SubtitleSoftConstraintWeight(kind, 0)).ToArray());
            Initial = new LayoutSubtitleBridgeSnapshot(projectId, 0, Root.Version, Root.Layout, Root.Profile.Width, Root.Profile.Height,
                [new LayoutSubtitleSourceBinding(Span.AssetId, OriginRef.FromAsset(Span.AssetId), new SourceDisplayGeometry(1920, 1080))], [],
                Root.Histories.Select(history => new LayoutSubtitleCueBinding(history, Root.Layout, profile, [], [])).ToArray(), [],
                [new LayoutSubtitleDisplayBinding(Root.Build.Subtitles.DisplayProfile, [])]);
            store = new SubtitleEditStore(DatabasePath);
            service = new DurableSubtitleEditService(store);
            service.RegisterGenerated(Initial, GeneratedInputDigest);
        }

        internal string DatabasePath { get; }
        internal ProjectStore Projects { get; }
        internal DefaultSettingsSnapshot Settings { get; }
        internal TranscriptRevision Transcript { get; }
        internal SourceSpan Span { get; }
        internal SourceSpan CutSpan { get; }
        internal SourceOnlyGeneratedSubtitleRuntimeEvidence Root { get; }
        internal LayoutSubtitleBridgeSnapshot Initial { get; }
        internal ContentDigest GeneratedInputDigest { get; } = Hash("test coordinator-bound generation checkpoint key");
        internal IReadOnlyList<StoredSubtitleEdit> Rows => store.ReadBranch(projectId, Root.Version.Id);

        internal SubtitleEditCapture Capture(EntityId<CompositionVersionIdTag>? root = null) => service.Capture(projectId, [root ?? Root.Version.Id]).Single();

        internal SubtitleEditCapture CapturePrefix(EntityId<CompositionVersionIdTag> root, int sequence, ContentDigest? digest) =>
            service.CapturePrefix(projectId, root, sequence, digest);

        internal LayoutSubtitleBridgeSnapshot Snapshot(SourceOnlyGeneratedSubtitleRuntimeEvidence output) =>
            new(projectId, 0, output.Version, output.Layout, output.Profile.Width, output.Profile.Height,
                Initial.Sources, Initial.Templates,
                output.Histories.Select(history => new LayoutSubtitleCueBinding(history, output.Layout,
                    SubtitleEditProjection.TargetLayoutProfile(output), [], [])).ToArray(), [],
                [new LayoutSubtitleDisplayBinding(output.Build.Subtitles.DisplayProfile, [])]);

        internal LayoutSubtitleBridgeSnapshot RegisterProjection(SubtitleEditProjectionResult result)
        {
            Projects.AppendCompositionVersion(result.Output.Version, CompositionSnapshotJson.WriteVersion(result.Output.Version));
            LayoutSubtitleBridgeSnapshot snapshot = Snapshot(result.Output);
            service.RegisterGenerated(snapshot, SubtitleEditProjection.GeneratedDigest(result.Output), projection: result.Evidence);
            return snapshot;
        }

        internal LayoutSubtitleBridgeSnapshot Edit(int cue, object edit) => Edit(Root.Version.Id, cue, edit);

        internal LayoutSubtitleBridgeSnapshot Edit(EntityId<CompositionVersionIdTag> root, int cue, object edit)
        {
            LayoutSubtitleBridgeSnapshot head = service.GetLatest(projectId, root);
            SubtitleCueRevision revision = head.Subtitles[cue].History.CurrentRevision;
            var request = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
            {
                projectId = projectId.ToString(),
                expectedRevision = head.Revision,
                expectedVersionId = head.CompositionVersion.Id.ToString(),
                expectedVersionDigest = head.CompositionVersion.ContentDigest.Hex,
                operation = new
                {
                    kind = "editSubtitle",
                    cueId = revision.Snapshot.Cue.Id.ToString(),
                    expectedRevisionId = revision.Id.ToString(),
                    expectedRevisionDigest = revision.ContentDigest.Hex,
                    reason = Reason,
                    edit,
                },
            }));
            LayoutSubtitleBridgeOperationResult result = service.Execute(request, Editor, CancellationToken.None);
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, result.Status);
            Assert.IsNotNull(result.Snapshot);
            return result.Snapshot;
        }

        internal object Placement()
        {
            SubtitlePlacementCandidate original = Root.Histories[0].CurrentRevision.Snapshot.ResolvedPlacement!;
            return new
            {
                kind = "placement",
                regionName = original.RegionName,
                rect = new
                {
                    x = Rational(new RationalNumber(3, 20)),
                    y = Rational(original.Rect.Y),
                    width = Rational(new RationalNumber(7, 10)),
                    height = Rational(original.Rect.Height)
                },
            };
        }

        internal SourceOnlyGeneratedSubtitleRuntimeEvidence Generate(bool repeated = false, TimeRange? cut = null,
            LayoutSpec? layout = null, TranscriptRevision? transcript = null, SourceSpan? span = null, int? maximumCueDuration = null,
            SourceSpan? cutSpan = null) =>
            Build(repeated, cut ?? Range(0, 10), layout, transcript ?? Transcript, span ?? Span, At.AddHours(2),
                maximumCueDuration is int maximum ? WithMaximumDuration(maximum) : Settings, cutSpan);

        private DefaultSettingsSnapshot WithMaximumDuration(int maximum) => settingsProfile.CreateSnapshot(Settings.Values.Select(selection =>
            selection.Key == "subtitles.cueMaximumDuration"
                ? new DefaultSettingSelection(selection.Key, DefaultSettingValue.FromInteger(maximum)) : selection).ToArray());

        private SourceOnlyGeneratedSubtitleRuntimeEvidence Build(bool repeated, TimeRange cut, LayoutSpec? layout,
            TranscriptRevision transcript, SourceSpan span, DateTimeOffset at, DefaultSettingsSnapshot? settingsOverride = null,
            SourceSpan? cutSpan = null)
        {
            DefaultSettingsSnapshot settings = settingsOverride ?? Settings;
            BundledRenderProfileDefinition actualRender = StandardRenderProfileFactory.CreateLandscape(settingsProfile, settings, reference,
                new RationalNumber(30, 1));
            layout ??= actualRender.Layout;
            cutSpan ??= span;
            CompositionElement[] elements = Enumerable.Range(0, repeated ? 2 : 1).Select(index => new CompositionElement(
                EntityId<CompositionElementIdTag>.New(), new TimeRange(index == 0 ? default : cut.Duration, cut.Duration),
                OriginRef.FromSourceSpan(cutSpan.Id), sourceRange: cut)).ToArray();
            var version = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), projectId, null, Editor, at,
                "Actual generated subtitle fixture from synthetic machine source", RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
            TimelineMap timeline = TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), version);
            StandardSubtitleBuildResult build = StandardSubtitleBuilder.BuildWithEvidence(version, timeline, layout, actualRender.Profile,
                transcript, Hash(TranscriptSnapshotJson.WriteRevision(transcript)), cutSpan.Id == span.Id ? [span] : [span, cutSpan], settingsProfile, settings,
                new RenderFontEvidence("Noto Sans JP", Hash("synthetic font evidence; no renderer invoked")), Editor, at);
            Assert.IsFalse(build.HasUnresolvedCues, string.Join(",", build.Warnings.Where(warning => warning.IsUnresolved).Select(warning => warning.Reason)));
            SubtitleCueHistory[] histories = build.Subtitles.Revisions.Select(revision => new SubtitleCueHistory(
                build.Subtitles.Candidates.Single(candidate => candidate.Id == revision.SourceCandidateId), [revision])).ToArray();
            return new(version, timeline, layout, actualRender.Profile, build, Array.AsReadOnly(histories));
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) { directory = directory.Parent; }
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}