using System.Globalization;
using System.Text;
using System.Text.Json;
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
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlySubtitleProjectionTests
{
    private const string Actor = "S-1-5-21-111-222-333-1001";

    [TestMethod]
    public void HistoricalPrefixRemainsExactAfterLaterEditsAndRestart()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot first = fixture.Edit(fixture.Initial, "Saved correction");
        StoredSubtitleEdit saved = fixture.Rows.Single();
        LayoutSubtitleBridgeSnapshot second = fixture.Edit(first, "Later correction");
        var restarted = new DurableSubtitleEditService(fixture.Store);
        restarted.RegisterGenerated(fixture.Initial, fixture.InputDigest);

        SubtitleEditCapture capture = restarted.CapturePrefix(fixture.Initial.ProjectId,
            fixture.Initial.CompositionVersion.Id, saved.Sequence, saved.RecordDigest);
        Assert.AreEqual(1, capture.Sequence);
        Assert.AreEqual(first.CompositionVersion.Id, capture.HeadVersionId);
        Assert.AreEqual(saved.RecordDigest, capture.Edits.Single().RecordDigest);
        Assert.AreEqual(saved.ResultSnapshotDigest, capture.HeadSnapshotDigest);
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(first), capture.HeadSnapshotDigest);
        Assert.AreEqual("Saved correction", capture.Head.Subtitles.Single().History.CurrentRevision.Snapshot.Text);
        Assert.IsFalse(capture.Head.CompositionVersions.ContainsKey(second.CompositionVersion.Id));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StoredSubtitleEdit>)capture.Edits).Clear());
        Assert.AreEqual(second.CompositionVersion.Id,
            restarted.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id).CompositionVersion.Id);
        _ = fixture.Edit(second, "Third correction");
        Assert.AreEqual(capture.HeadSnapshotDigest, restarted.CapturePrefix(fixture.Initial.ProjectId,
            fixture.Initial.CompositionVersion.Id, saved.Sequence, saved.RecordDigest).HeadSnapshotDigest);
        Assert.AreEqual(3, fixture.Rows.Count);
    }

    [TestMethod]
    public void PrefixRejectsWrongDigestMissingSequenceAndCancellation()
    {
        using var fixture = new Fixture();
        _ = fixture.Edit(fixture.Initial, "Saved correction");
        StoredSubtitleEdit saved = fixture.Rows.Single();
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.CapturePrefix(
            fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id, 1, Hash("wrong prefix")));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.CapturePrefix(
            fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id, 2, saved.RecordDigest));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Service.CapturePrefix(
            fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id, -1, saved.RecordDigest));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Service.CapturePrefix(
            fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id, 1, saved.RecordDigest, canceled.Token));
        Assert.AreEqual(1, fixture.Rows.Count);
    }

    [TestMethod]
    public void EmptyPrefixReturnsInitialWithoutRollingBackCurrentHead()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot edited = fixture.Edit(fixture.Initial, "Later correction");
        SubtitleEditCapture capture = fixture.Service.CapturePrefix(fixture.Initial.ProjectId,
            fixture.Initial.CompositionVersion.Id, 0, null);
        Assert.AreEqual(0, capture.Sequence);
        Assert.AreSame(fixture.Initial, capture.Head);
        Assert.AreEqual(capture.InitialSnapshotDigest, capture.HeadSnapshotDigest);
        Assert.AreSame(edited, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
    }

    [TestMethod]
    public void HistoricalCaptureDoesNotHideCorruptionAfterTheRequestedPrefix()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot first = fixture.Edit(fixture.Initial, "Saved correction");
        StoredSubtitleEdit saved = fixture.Rows.Single();
        _ = fixture.Edit(first, "Later correction");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER trg_subtitle_edits_update; UPDATE subtitle_edits SET commit_digest = $digest WHERE sequence = 2;";
        command.Parameters.AddWithValue("$digest", Hash("corrupt suffix").Hex);
        command.ExecuteNonQuery();
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.CapturePrefix(
            fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id, saved.Sequence, saved.RecordDigest));
    }

    [TestMethod]
    public void PipelineProjectionReplacesLandscapeAndShortInputsTogetherAndPreservesThePureBase()
    {
        using var fixture = new PipelineProjectionFixture();
        SourceOnlyGeneratedRuntimeEvidence previous = fixture.Initial;
        SubtitleEditCapture[] captures = previous.Versions.Values.Select((item, index) =>
            fixture.EditAndCapture(previous, item, index == 0 ? "Saved landscape correction" : "Saved short correction")).ToArray();
        var (execution, generated) = fixture.Next(fixture.InitialExecution, previous, captures);
        string before = LegacyGeneratedJson(execution, generated);
        SourceOnlyGeneratedRuntimeEvidence projected = SourceOnlyPipeline.ProjectCapturedSubtitleEdits(
            execution, generated, fixture.InitialExecution, previous, captures);

        Assert.AreSame(generated.Preparation, projected.Preparation);
        Assert.AreSame(generated.Import, projected.Import);
        Assert.AreEqual(before, LegacyGeneratedJson(execution, generated));
        Assert.AreEqual(TranscriptChangeSource.Machine, projected.Import.Revision.ChangeSource);
        Assert.AreEqual(generated.CreatedAt, projected.CreatedAt);
        Assert.AreSame(projected.LandscapeSubtitles, projected.Versions[projected.Preparation.Child.Id].Build);
        Assert.AreEqual("Saved landscape correction", projected.LandscapeSubtitles.Subtitles.Revisions[0].Snapshot.Text);
        Assert.AreEqual("Saved short correction", projected.ShortForms.Single().Subtitles.Revisions[0].Snapshot.Text);
        foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in projected.Versions.Values)
        {
            Assert.IsNotNull(item.SubtitleProjection);
            SubtitleProjectionCutCatalog catalog = item.SubtitleProjection.CutCatalog
                ?? throw new AssertFailedException("Projection must bind the actual production cut spans.");
            CollectionAssert.AreEquivalent(item.Timeline.Entries.Select(entry => entry.Origin.SourceSpanId!.ToString()).Distinct().ToArray(),
                catalog.TargetSpans.Select(span => span.Id.ToString()).ToArray());
            Assert.IsTrue(catalog.TargetSpans.All(span => generated.Preparation.Plan.LongForm.Provenance.SourceSpans
                .Concat(generated.Preparation.Plan.ShortForms.SelectMany(shortForm => shortForm.SourceSpans))
                .Any(planned => SubtitleProjectionCutCatalog.SameBinding(planned, span))));
            Assert.AreSame(item.SubtitleProjection, item.Build.Subtitles.Projection);
            Assert.IsTrue(item.Histories.All(history => history.Revisions.Count == 1 && history.Inheritance.Count == 0));
            Assert.AreEqual(PipelineProjectionFixture.Coordinator, item.Histories[0].CurrentRevision.ActorSid);
            Assert.AreEqual(generated.CreatedAt, item.Histories[0].CurrentRevision.CreatedAt);
            item.SubtitleProjection.ValidateOutput(item);
            StandardRenderGraphFactory.ValidateSubtitleProjection(projected.Preparation, item.Build.Subtitles,
                item.Version.Id == projected.Preparation.Child.Id ? null : projected.ShortForms.Single().ShortFormId);
        }
        var shortInput = projected.ShortForms.Single();
        Assert.AreNotEqual(previous.ShortForms.Single().ShortFormId, shortInput.ShortFormId,
            "A regenerated short must be matched by source sequence rather than its derived plan ID.");
        Assert.AreSame(shortInput.Subtitles, projected.Versions[
            StandardRenderGraphFactory.CreateShortVersion(projected.Preparation, shortInput.ShortFormId).Id].Build.Subtitles);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IDictionary<EntityId<CompositionVersionIdTag>, SourceOnlyGeneratedSubtitleRuntimeEvidence>)projected.Versions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StandardShortArtifactInput>)projected.ShortForms).Clear());

        string json = SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(execution, projected);
        using JsonDocument checkpoint = JsonDocument.Parse(json);
        Assert.AreEqual("source-only.generated-runtime.v2", checkpoint.RootElement.GetProperty("schema").GetString());
        Assert.AreEqual(2, checkpoint.RootElement.GetProperty("projections").GetArrayLength());
        foreach (JsonElement proof in checkpoint.RootElement.GetProperty("projections").EnumerateArray())
        {
            var item = projected.Versions[EntityId<CompositionVersionIdTag>.Parse(proof.GetProperty("versionId").GetString()!)];
            Assert.AreEqual(item.SubtitleProjection!.CanonicalJson, proof.GetProperty("canonicalJson").GetString());
            Assert.AreEqual(item.SubtitleProjection.ContentDigest.Hex, proof.GetProperty("digest").GetString());
            Assert.AreEqual(fixture.InitialExecution.Request.ExecutionId.ToString(), proof.GetProperty("sourceExecutionId").GetString());
            Assert.AreEqual(item.SubtitleProjectionSource!.RootVersionId.ToString(), proof.GetProperty("sourceRootVersionId").GetString());
            Assert.AreEqual(1, proof.GetProperty("capturedSequence").GetInt32());
        }
        Assert.AreEqual(json, SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(execution,
            SourceOnlyPipeline.ProjectCapturedSubtitleEdits(execution, generated, fixture.InitialExecution, previous, captures)));
        Assert.AreEqual("source_only.subtitle_projection_conflict", Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(execution, generated)).Code);
        Assert.AreEqual("source_only.subtitle_projection_conflict", Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.ProjectCapturedSubtitleEdits(execution, generated, fixture.InitialExecution, previous, captures.Take(1).ToArray())).Code);
        var stripped = projected.Versions.ToDictionary(pair => pair.Key, pair => pair.Value with { SubtitleProjection = null });
        Assert.AreEqual("source_only.subtitle_projection_conflict", Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(execution, projected with { Versions = stripped })).Code);
        foreach (var capture in captures) { Assert.AreEqual(1, fixture.Store.ReadBranch(capture.ProjectId, capture.RootVersionId).Count); }
    }

    [TestMethod]
    public void PipelineCarriesPriorProofWithAnEmptyAcceptedPrefixAndNeverImportsALaterEdit()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        SubtitleEditCapture original = fixture.EditAndCapture(fixture.Initial, fixture.Initial.Versions.Single().Value, "Accepted correction");
        var (firstExecution, firstBase) = fixture.Next(fixture.InitialExecution, fixture.Initial, [original]);
        SourceOnlyGeneratedRuntimeEvidence first = SourceOnlyPipeline.ProjectCapturedSubtitleEdits(
            firstExecution, firstBase, fixture.InitialExecution, fixture.Initial, [original]);
        fixture.Register(first);
        SourceOnlyGeneratedSubtitleRuntimeEvidence firstItem = first.Versions.Single().Value;
        var restarted = new DurableSubtitleEditService(fixture.Store);
        restarted.RegisterGenerated(SourceOnlyApplicationRegistrations.CreateLayout(first, firstItem, null),
            SourceOnlyApplicationRegistrations.GeneratedKey(first, firstItem), projection: firstItem.SubtitleProjection);
        SubtitleEditCapture empty = restarted.CapturePrefix(first.Preparation.Child.ProjectId, firstItem.Version.Id, 0, null);
        _ = fixture.EditAndCapture(first, firstItem, "Changed after acceptance");
        SubtitleEditCapture prefix = restarted.CapturePrefix(first.Preparation.Child.ProjectId, firstItem.Version.Id, 0, null);
        Assert.AreEqual(empty.HeadSnapshotDigest, prefix.HeadSnapshotDigest);
        Assert.AreSame(firstItem.SubtitleProjection, prefix.InitialProjection);
        var completed = fixture.Completed(firstExecution, first);
        var (nextExecution, nextBase) = fixture.Next(completed, first, [prefix]);
        Assert.IsEmpty(nextExecution.Request.SubtitleEdits!);
        SourceOnlyGeneratedRuntimeEvidence second = SourceOnlyPipeline.ProjectCapturedSubtitleEdits(
            nextExecution, nextBase, completed, first, [prefix]);
        SourceOnlyGeneratedSubtitleRuntimeEvidence item = second.Versions.Single().Value;
        Assert.AreEqual("Accepted correction", item.Histories[0].CurrentRevision.Snapshot.Text);
        Assert.AreEqual(2, item.SubtitleProjection!.ProjectionDepth);
        Assert.AreEqual(0, item.SubtitleProjectionSource!.CapturedSequence);
        Assert.AreEqual(firstExecution.Request.ExecutionId, item.SubtitleProjectionSource.ExecutionId);
        Assert.AreEqual(original.Edits.Single().RecordDigest,
            item.SubtitleProjection.GetInheritedFields(item.Histories[0].CueId).Single().Record.RecordDigest);
        Assert.AreEqual(1, fixture.Store.ReadBranch(firstItem.Version.ProjectId, firstItem.Version.Id).Count);
        using JsonDocument checkpoint = JsonDocument.Parse(SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(nextExecution, second));
        Assert.AreEqual("source-only.generated-runtime.v2", checkpoint.RootElement.GetProperty("schema").GetString());
        Assert.AreEqual(0, checkpoint.RootElement.GetProperty("projections")[0].GetProperty("capturedSequence").GetInt32());
    }

    [TestMethod]
    public void EditedShortWithoutASourceIdenticalSuccessorFailsInsteadOfReturningLandscapeOnly()
    {
        using var fixture = new PipelineProjectionFixture();
        var oldShort = fixture.Initial.Versions.Values.Single(item => item.Version.Id != fixture.Initial.Preparation.Child.Id);
        SubtitleEditCapture capture = fixture.EditAndCapture(fixture.Initial, oldShort, "Keep this short edit");
        var (execution, target) = fixture.Next(fixture.InitialExecution, fixture.Initial, [capture], includeShort: false);
        SourceOnlyPipeline.PipelineFailure failure = Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.ProjectCapturedSubtitleEdits(execution, target, fixture.InitialExecution, fixture.Initial, [capture]));
        Assert.AreEqual("source_only.subtitle_projection_conflict", failure.Code);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, failure.Phase);
        Assert.IsTrue(target.Versions.Values.All(item => item.SubtitleProjection is null));
        Assert.AreEqual(1, fixture.Store.ReadBranch(capture.ProjectId, capture.RootVersionId).Count);
    }

    [TestMethod]
    public void ExactResultBearingParentSelectionNeverFallsBackToTheNewestValidExecution()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        var (execution, target) = fixture.Next(fixture.InitialExecution, fixture.Initial, []);
        StoredSourceOnlyExecution newer = fixture.Completed(execution, target);
        Assert.AreSame(fixture.InitialExecution, SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [newer, fixture.InitialExecution]));
        Assert.AreEqual("source_only.subtitle_projection_conflict", Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [newer])).Code);
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [fixture.InitialExecution, fixture.InitialExecution]));
        StoredSourceOnlyExecution wrongDigest = PipelineProjectionFixture.Stored(execution.Request with { ParentDigest = Hash("wrong parent") });
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.FindSubtitleProjectionParent(wrongDigest, [fixture.InitialExecution]));
    }

    [TestMethod]
    public void ExactTypedResultBearingFailedParentIsSelectedWithoutRequiringQcSuccess()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        var (execution, target) = fixture.Next(fixture.InitialExecution, fixture.Initial, []);
        StoredSourceOnlyExecution failed = PipelineProjectionFixture.Stored(fixture.InitialExecution.Request,
            fixture.Initial, SourceOnlyExecutionPhase.Failed);
        StoredSourceOnlyExecution newer = fixture.Completed(execution, target);
        Assert.IsTrue(SourceOnlyResult.ParseCanonical(failed.ResultJson!, failed.ResultDigest).HasBlockingFailures);
        Assert.AreSame(failed, SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [newer, failed]));
        Assert.AreSame(target, SourceOnlyPipeline.ProjectCapturedSubtitleEdits(execution, target, failed, fixture.Initial, []));

        // Even a successful candidate must not hide another owner of the same binding.
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [fixture.InitialExecution, failed]));
    }

    [TestMethod]
    public void ParentSelectionRejectsMissingUntypedAndMisboundResultsWithoutFallback()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        var (execution, target) = fixture.Next(fixture.InitialExecution, fixture.Initial, []);
        StoredSourceOnlyExecution newer = fixture.Completed(execution, target);
        foreach (SourceOnlyExecutionPhase phase in new[] { SourceOnlyExecutionPhase.Completed, SourceOnlyExecutionPhase.Failed })
        {
            StoredSourceOnlyExecution parent = PipelineProjectionFixture.Stored(fixture.InitialExecution.Request, fixture.Initial, phase);
            SourceOnlyResult result = SourceOnlyResult.ParseCanonical(parent.ResultJson!, parent.ResultDigest);
            string bindingOnly = SourceOnlyResultCodec.Write(new { binding = SourceOnlyResultCodec.BindingDto(result.Binding) });
            foreach (string? json in new[] { null, "{}", bindingOnly, newer.ResultJson })
            {
                var invalid = new StoredSourceOnlyExecution(parent.Request, parent.RequestJson, parent.RequestDigest,
                    parent.SettingsRevision, parent.FinalizationDigest, parent.LocatorDigest, parent.Revision, parent.Phase,
                    json, json is null ? null : Hash(json), parent.VersionId, parent.VersionDigest, parent.FailureCode, parent.StateDigest);
                Assert.AreEqual("source_only.subtitle_projection_conflict", Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                    SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [newer, invalid])).Code);
                Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                    SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [parent, invalid]));
            }
        }
    }

    [TestMethod]
    public void TypedResultsDoNotMakeOtherPhasesEligibleForParentSelection()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        var (execution, _) = fixture.Next(fixture.InitialExecution, fixture.Initial, []);
        foreach (SourceOnlyExecutionPhase phase in Enum.GetValues<SourceOnlyExecutionPhase>()
            .Where(value => value is not (SourceOnlyExecutionPhase.Completed or SourceOnlyExecutionPhase.Failed)))
        {
            StoredSourceOnlyExecution parent = PipelineProjectionFixture.Stored(fixture.InitialExecution.Request, fixture.Initial, phase);
            Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                SourceOnlyPipeline.FindSubtitleProjectionParent(execution, [parent]));
        }
    }

    [TestMethod]
    public void TwentyUneditedAcceptanceOwnersNeedNeitherResultJsonNorRecursiveMediaProof()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        StoredSourceOnlyExecution[] chain = AcceptanceChain(fixture.InitialExecution.Request, 20);
        Assert.IsTrue(chain.Length > SourceOnlyPipeline.MaximumSubtitleProjectionParentDepth);
        Assert.IsTrue(chain.All(item => item.ResultJson is null && item.ResultDigest is null));
        Assert.IsFalse(SourceOnlyPipeline.RequiresSubtitleProjection(chain[^1], chain.Reverse().ToArray()));
        Assert.IsFalse(SourceOnlyPipeline.RequiresSubtitleProjection(chain[0], chain));
    }

    [TestMethod]
    public void CapturedHeadsAnywhereInAcceptanceHistoryAlwaysRequireProofRederivation()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        foreach (int captureIndex in new[] { 1, 10, 19 })
        {
            StoredSourceOnlyExecution[] chain = AcceptanceChain(fixture.InitialExecution.Request, 20, captureIndex);
            Assert.AreEqual(captureIndex == 19, chain[^1].Request.SubtitleEdits is { Count: > 0 });
            Assert.IsTrue(SourceOnlyPipeline.RequiresSubtitleProjection(chain[^1], chain));
        }
    }

    [TestMethod]
    public void UnacceptedCurrentJournalAndUnrelatedBranchHeadsDoNotAuthorizeProjection()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        SubtitleEditCapture edit = fixture.EditAndCapture(fixture.Initial, fixture.Initial.Versions.Single().Value,
            "Not captured by this acceptance");
        var (execution, _) = fixture.Next(fixture.InitialExecution, fixture.Initial, []);
        StoredSourceOnlyExecution[] unrelated = AcceptanceChain(fixture.InitialExecution.Request, 3, captureIndex: 1);
        Assert.AreEqual(1, fixture.Store.ReadBranch(edit.ProjectId, edit.RootVersionId).Count);
        Assert.IsFalse(SourceOnlyPipeline.RequiresSubtitleProjection(execution, [fixture.InitialExecution, .. unrelated]));
        Assert.AreEqual(1, fixture.Store.ReadBranch(edit.ProjectId, edit.RootVersionId).Count);
    }

    [TestMethod]
    public void MissingOlderOwnerIsLegacyOnlyWhenNoAcceptedHeadRequiresProjection()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        StoredSourceOnlyExecution[] unedited = AcceptanceChain(fixture.InitialExecution.Request, 20);
        Assert.IsFalse(SourceOnlyPipeline.RequiresSubtitleProjection(unedited[^1], unedited.Skip(1).ToArray()));
        Assert.IsFalse(SourceOnlyPipeline.RequiresSubtitleProjection(unedited[^1], []));
        StoredSourceOnlyExecution[] captured = AcceptanceChain(fixture.InitialExecution.Request, 20, captureIndex: 19);
        Assert.IsTrue(SourceOnlyPipeline.RequiresSubtitleProjection(captured[^1], captured.Skip(1).ToArray()));
        Assert.IsTrue(SourceOnlyPipeline.RequiresSubtitleProjection(captured[^1], []));
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.FindSubtitleProjectionParent(captured[^1], []));
    }

    [TestMethod]
    public void AcceptanceOwnerConflictsNeverBecomeAnUneditedLegacyFallback()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        StoredSourceOnlyExecution[] chain = AcceptanceChain(fixture.InitialExecution.Request, 3);
        StoredSourceOnlyExecution owner = chain[1], current = chain[2];
        foreach (SourceOnlyExecutionRequest request in new[]
        {
            owner.Request with { ProjectId = EntityId<ProjectIdTag>.New() },
            owner.Request with { DraftId = EntityId<ProjectCreationDraftIdTag>.New() },
            owner.Request with { IngestJobId = EntityId<JobIdTag>.New() },
            owner.Request with { SourceCandidateId = EntityId<AssetIdTag>.New() },
            owner.Request with { RootJobId = current.Request.RootJobId },
            owner.Request with { ExecutionId = current.Request.ExecutionId },
            owner.Request with { CreatedAt = current.Request.CreatedAt },
            owner.Request with { CreatedAt = current.Request.CreatedAt.AddSeconds(1) },
        })
        {
            StoredSourceOnlyExecution invalid = AcceptanceOwner(request, owner.VersionId!, owner.VersionDigest!);
            Assert.AreEqual("source_only.subtitle_projection_conflict", Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                SourceOnlyPipeline.RequiresSubtitleProjection(current, [chain[0], invalid, current])).Code);
        }
        StoredSourceOnlyExecution wrongDigest = AcceptanceOwner(owner.Request, owner.VersionId!, Hash("wrong owner digest"));
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.RequiresSubtitleProjection(current, [chain[0], wrongDigest, current]));
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.RequiresSubtitleProjection(current, [.. chain, owner]));
        StoredSourceOnlyExecution cycle = AcceptanceOwner(chain[0].Request with
        {
            ParentVersionId = current.VersionId,
            ParentDigest = current.VersionDigest,
        }, chain[0].VersionId!, chain[0].VersionDigest!);
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.RequiresSubtitleProjection(current, [cycle, owner, current]));
        StoredSourceOnlyExecution missingDigest = AcceptanceOwner(current.Request with { ParentDigest = null },
            current.VersionId!, current.VersionDigest!);
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.RequiresSubtitleProjection(missingDigest, chain));
    }

    [TestMethod]
    public void AcceptanceOwnerWalkAccepts256NodesAndRejectsAnOverflowOrTruncatedHistory()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        StoredSourceOnlyExecution[] chain = AcceptanceChain(fixture.InitialExecution.Request,
            SourceOnlyExecutionStore.MaximumVersionLineageLength + 1);
        Assert.IsFalse(SourceOnlyPipeline.RequiresSubtitleProjection(chain[^2], chain));
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.RequiresSubtitleProjection(chain[^1], chain));
        Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() => SourceOnlyPipeline.RequiresSubtitleProjection(chain[^2],
            Enumerable.Repeat(chain[0], SourceOnlyExecutionStore.MaximumListResults).ToArray()));
    }

    [TestMethod]
    public void ProjectionParentJobsRequireTheExactTerminalOwnerAndAllowQcFailure()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        foreach (SourceOnlyExecutionPhase phase in new[] { SourceOnlyExecutionPhase.Completed, SourceOnlyExecutionPhase.Failed })
        {
            StoredSourceOnlyExecution parent = PipelineProjectionFixture.Stored(fixture.InitialExecution.Request, fixture.Initial, phase);
            JobState expected = phase == SourceOnlyExecutionPhase.Completed ? JobState.Succeeded : JobState.Failed;
            StoredJob JobValue(JobState state, EntityId<JobIdTag>? id = null, EntityId<ProjectIdTag>? project = null,
                string? kind = null, DateTimeOffset? createdAt = null) => new(new Job(id ?? parent.Request.RootJobId,
                    project ?? parent.Request.ProjectId, kind ?? SourceOnlyExecutionStore.RootJobKind, state,
                    createdAt ?? parent.Request.CreatedAt, fixture.Initial.CreatedAt.AddMinutes(1), 1), 2);
            SourceOnlyResult result = SourceOnlyPipeline.ValidateProjectionParentJob(parent, JobValue(expected));
            Assert.AreEqual(parent.ResultJson, result.CanonicalJson);
            Assert.IsTrue(result.HasBlockingFailures, "QC failure does not erase the generated input evidence.");
            foreach (JobState state in Enum.GetValues<JobState>().Where(value => value != expected))
                Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                    SourceOnlyPipeline.ValidateProjectionParentJob(parent, JobValue(state)));
            foreach (StoredJob invalid in new[]
            {
                JobValue(expected, id: EntityId<JobIdTag>.New()),
                JobValue(expected, project: EntityId<ProjectIdTag>.New()),
                JobValue(expected, kind: SourceOnlyExecutionStore.IngestJobKind),
                JobValue(expected, createdAt: parent.Request.CreatedAt.AddSeconds(1)),
            })
                Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                    SourceOnlyPipeline.ValidateProjectionParentJob(parent, invalid));
            StoredSourceOnlyExecution noResult = AcceptanceOwner(parent.Request, parent.VersionId!, parent.VersionDigest!);
            Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                SourceOnlyPipeline.ValidateProjectionParentJob(noResult, JobValue(JobState.Failed)));
        }
    }

    [TestMethod]
    public void FailedGenerationHeadersAndNonQcFailuresAreNotProjectionParentProof()
    {
        using var fixture = new PipelineProjectionFixture(includeShort: false);
        StoredSourceOnlyExecution parent = PipelineProjectionFixture.Stored(fixture.InitialExecution.Request,
            fixture.Initial, SourceOnlyExecutionPhase.Failed);
        SourceOnlyResult original = SourceOnlyResult.ParseCanonical(parent.ResultJson!, parent.ResultDigest);
        var job = new StoredJob(new Job(parent.Request.RootJobId, parent.Request.ProjectId,
            SourceOnlyExecutionStore.RootJobKind, JobState.Failed, parent.Request.CreatedAt,
            fixture.Initial.CreatedAt.AddMinutes(1), 1), 2);
        SourceOnlyResult[] invalidResults =
        [
            new(original.Binding, StandardArtifactGenerationDisposition.Failed, [], [],
                [new(SourceOnlyResultFailureCode.GenerationFailed), new(SourceOnlyResultFailureCode.NoArtifacts),
                    new(SourceOnlyResultFailureCode.PrimaryArtifactMissing)], [], [], null, false),
            new(original.Binding, StandardArtifactGenerationDisposition.Completed, original.Artifacts, [],
                [new(SourceOnlyResultFailureCode.StoredEvidenceInvalid)], [], [], null, false),
            new(original.Binding, StandardArtifactGenerationDisposition.Completed, original.Artifacts, [],
                [], [], [], null, true),
        ];
        foreach (SourceOnlyResult result in invalidResults)
        {
            var invalid = new StoredSourceOnlyExecution(parent.Request, parent.RequestJson, parent.RequestDigest,
                parent.SettingsRevision, parent.FinalizationDigest, parent.LocatorDigest, parent.Revision, parent.Phase,
                result.CanonicalJson, result.CanonicalDigest, parent.VersionId, parent.VersionDigest, parent.FailureCode, parent.StateDigest);
            Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                SourceOnlyPipeline.ValidateProjectionParentJob(invalid, job));
        }
    }

    [TestMethod]
    public void UneditedParentReturnsTheSameBaseAndUsesTheExactLegacyV1Bytes()
    {
        using var fixture = new PipelineProjectionFixture();
        Assert.AreEqual(LegacyGeneratedJson(fixture.InitialExecution, fixture.Initial),
            SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(fixture.InitialExecution, fixture.Initial));
        Assert.AreEqual(StandardSubtitleBuilder.FullCoverageRuleVersion, fixture.Initial.BuilderRule);
        var (execution, target) = fixture.Next(fixture.InitialExecution, fixture.Initial, []);
        SourceOnlyGeneratedRuntimeEvidence projected = SourceOnlyPipeline.ProjectCapturedSubtitleEdits(
            execution, target, fixture.InitialExecution, fixture.Initial, []);
        Assert.AreSame(target, projected);
        Assert.AreEqual(LegacyGeneratedJson(execution, target), SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(execution, projected));
    }

    [TestMethod]
    public void ShortSourceIdentityIgnoresDerivedIdsButRequiresEveryOrderedSourceField()
    {
        using var fixture = new Fixture();
        SourceSpan source = fixture.Initial.Subtitles.Single().History.SourceCandidate.SourceSpan;
        SourceSpan Copy(EntityId<AssetIdTag>? asset = null, ContentDigest? digest = null, string? stream = null,
            EntityId<TranscriptRevisionIdTag>? revision = null, IReadOnlyList<EntityId<TranscriptTokenIdTag>>? tokens = null, TimeRange? range = null) =>
            new(EntityId<SourceSpanIdTag>.New(), asset ?? source.AssetId, stream ?? source.StreamId, range ?? source.Range,
                digest ?? source.SourceDigest, revision ?? source.TranscriptRevisionId, tokens ?? source.TranscriptTokenIds);
        Assert.IsTrue(SourceOnlyPipeline.SameSubtitleSourceSpans([source], [Copy()]));
        foreach (SourceSpan changed in new[] { Copy(asset: EntityId<AssetIdTag>.New()), Copy(digest: Hash("other source")), Copy(stream: "audio:99"),
            Copy(revision: EntityId<TranscriptRevisionIdTag>.New()), Copy(tokens: [EntityId<TranscriptTokenIdTag>.New()]), Copy(range: Range(1, 2)) })
            Assert.IsFalse(SourceOnlyPipeline.SameSubtitleSourceSpans([source], [changed]));
        SourceSpan second = Copy(range: Range(2, 2));
        Assert.IsFalse(SourceOnlyPipeline.SameSubtitleSourceSpans([source, second], [second, source]));
        Assert.IsFalse(SourceOnlyPipeline.SameSubtitleSourceSpans([source, source], [source]));
        EntityId<TranscriptTokenIdTag>[] tokens = [.. source.TranscriptTokenIds, EntityId<TranscriptTokenIdTag>.New()];
        Assert.IsFalse(SourceOnlyPipeline.SameSubtitleSourceSpans([Copy(tokens: tokens)], [Copy(tokens: tokens.Reverse().ToArray())]));
    }

    // This is the previous production v1 expression, kept independent of the new
    // writer so a changed property, casing, number or extra null fails the regression.
    private static string LegacyGeneratedJson(StoredSourceOnlyExecution execution, SourceOnlyGeneratedRuntimeEvidence evidence) =>
        CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "source-only.generated-runtime.v1",
            executionId = execution.Request.ExecutionId.ToString(),
            projectId = execution.Request.ProjectId.ToString(),
            settingsDigest = execution.Request.SettingsSnapshot.ContentDigest.Hex,
            productionVersionId = evidence.Preparation.Child.Id.ToString(),
            productionVersionDigest = evidence.Preparation.Child.ContentDigest.Hex,
            planDigest = evidence.Preparation.Plan.PlanDigest.Hex,
            transcriptRevisionId = evidence.Import.Revision.Id.ToString(),
            transcriptDigest = Hash(TranscriptSnapshotJson.WriteRevision(evidence.Import.Revision)).Hex,
            createdAt = evidence.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            font = evidence.Font,
            // FR-051: the rule is per evidence (v3 for new runs, v2 when replaying older results).
            builderRule = evidence.BuilderRule,
            projectionRule = "source-only.subtitle-projection.v1",
            versions = evidence.Versions.Values.OrderBy(item => item.Version.Id.ToString(), StringComparer.Ordinal).Select(item => new
            {
                versionId = item.Version.Id.ToString(),
                versionDigest = item.Version.ContentDigest.Hex,
                versionSnapshotDigest = Hash(CompositionSnapshotJson.WriteVersion(item.Version)).Hex,
                timeline = item.Timeline,
                layout = item.Layout,
                profile = item.Profile,
                build = item.Build,
            }).ToArray(),
            shortForms = evidence.ShortForms.Select(item => item.ShortFormId.ToString()).ToArray(),
        }));

    private static ContentDigest Hash(string value) => ContentDigest.Compute(Encoding.UTF8.GetBytes(value));
    private static TimeRange Range(long start, long duration) => new(new RationalTime(start, 1), new RationalTime(duration, 1));

    // Acceptance-only legacy fixtures deliberately have no typed result/media proof.
    // They must be enough to rule OUT projection, never to authorize projected fields.
    private static StoredSourceOnlyExecution AcceptanceOwner(SourceOnlyExecutionRequest request,
        EntityId<CompositionVersionIdTag> versionId, ContentDigest versionDigest) => new(request,
            "{\"syntheticAcceptanceFixture\":true}", Hash("acceptance fixture"), 0, null, null, 1,
            SourceOnlyExecutionPhase.Failed, null, null, versionId, versionDigest,
            "source_only.legacy_fixture", Hash("acceptance state fixture"));

    private static StoredSourceOnlyExecution[] AcceptanceChain(SourceOnlyExecutionRequest seed, int count, int? captureIndex = null)
    {
        var chain = new List<StoredSourceOnlyExecution>();
        for (int index = 0; index < count; index++)
        {
            StoredSourceOnlyExecution? previous = chain.LastOrDefault();
            SourceOnlyExecutionRequest request = seed with
            {
                ExecutionId = EntityId<SourceOnlyExecutionIdTag>.New(),
                RootJobId = EntityId<JobIdTag>.New(),
                CreatedAt = seed.CreatedAt.AddMinutes(index),
                ParentVersionId = previous?.VersionId,
                ParentDigest = previous?.VersionDigest,
                SubtitleEdits = index == captureIndex
                    ? [new SubtitleEditHeadReference(previous!.VersionId!, 1, Hash("accepted fixture prefix"))]
                    : index % 2 == 0 ? null : [],
            };
            chain.Add(AcceptanceOwner(request, EntityId<CompositionVersionIdTag>.New(), Hash("fixture version " + index)));
        }
        return chain.ToArray();
    }

    // Real pure planner/builder and schema-012 subtitle journal; execution records
    // below are explicit selection fixtures, NOT completed media/QC/delivery evidence.
    private sealed class PipelineProjectionFixture : IDisposable
    {
        internal const string Coordinator = "S-1-5-21-111-222-333-1002";
        private static readonly DateTimeOffset At = new(2038, 4, 5, 6, 7, 8, TimeSpan.Zero);
        private readonly string directory = Path.Combine(Path.GetTempPath(), "HubRadio.PipelineSubtitleProjection", Guid.NewGuid().ToString("N"));
        private readonly DefaultSettingsProfile profile;
        private readonly DefaultSettingsSnapshot settings;
        private readonly byte[] landscapeJson;
        private readonly byte[] verticalJson;
        private readonly Project project;
        private readonly ProjectStore projects;
        private readonly TranscriptImportResult import;
        private readonly SourceSpan full;
        private readonly DurableSubtitleEditService service;
        private readonly bool includeShort;

        internal PipelineProjectionFixture(bool includeShort = true)
        {
            this.includeShort = includeShort;
            Directory.CreateDirectory(directory);
            string database = Path.Combine(directory, "store.db");
            _ = DatabaseMigrator.Migrate(database, Path.Combine(directory, "backups"));
            projects = new ProjectStore(database);
            Store = new SubtitleEditStore(database);
            service = new DurableSubtitleEditService(Store);
            profile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            settings = profile.CreateSnapshot();
            landscapeJson = File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json"));
            verticalJson = File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/9x16/vertical-short.json"));
            var audio = new AnalysisAudioTimeline(EntityId<ProjectIdTag>.New(), EntityId<JobIdTag>.New(), EntityId<AssetIdTag>.New(),
                Hash("synthetic source"), 100, EntityId<ArtifactIdTag>.New(), Hash("synthetic PTS"), 0, 1,
                new RationalTime(1, 48000), default, 0, 1024, EntityId<ArtifactIdTag>.New(), Hash("synthetic audio"),
                640044, 320000, EntityId<ArtifactIdTag>.New());
            var worker = new AsrWorkerResult("Systran/faster-whisper-small", "536b0662742c02347bc0e980a01041f333bce120",
                new ContentDigest("4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"),
                new AsrWorkerConfiguration("cpu", "int8", "ja", "transcribe", 5, true, false, true),
                audio.AnalysisAudioDigest, audio.AnalysisAudioByteSize, 20, "ja", 0.9, 20,
                [new AsrWorkerSegment(0, 0, "実測。 確認。 結論。", 2, 17, -0.1, 0.01,
                    [new AsrWorkerToken("実測。", 2, 4, 0.9, false), new AsrWorkerToken("確認。", 8, 10, 0.9, false),
                        new AsrWorkerToken("結論。", 15, 17, 0.9, false)])]);
            import = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(worker, audio,
                EntityId<TranscriptRevisionIdTag>.New(), EntityId<TranscriptSpeakerIdTag>.New(), At));
            full = new SourceSpan(EntityId<SourceSpanIdTag>.New(), audio.SourceAssetId, "audio:1", Range(0, 20),
                audio.SourceContentDigest, import.Revision.Id, import.Revision.Utterances.SelectMany(value => value.Tokens).Select(value => value.Id).ToArray());
            var asset = new Asset(audio.SourceAssetId, "synthetic.mov", "video/quicktime", audio.SourceContentDigest, audio.SourceByteSize,
                new RightsRecord(RightsStatus.Unconfirmed, null, "Synthetic projection input, not permission to render", false));
            project = new Project(audio.ProjectId, "synthetic", "Prefix replay", "Editors", PublicationScope.InternalOnly, null, [asset], []);
            projects.CreateProject(project, HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project), At);
            var element = new CompositionElement(EntityId<CompositionElementIdTag>.New(), Range(0, 20), OriginRef.FromSourceSpan(full.Id), sourceRange: full.Range);
            var baseline = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), project.Id, null, Actor, At,
                "Synthetic source composition", RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]), [element]);
            projects.AppendCompositionVersion(baseline, CompositionSnapshotJson.WriteVersion(baseline));
            Initial = Generate(baseline, Actor, At.AddMinutes(1), includeShort);
            InitialExecution = Stored(new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), project.Id,
                EntityId<ProjectCreationDraftIdTag>.New(), audio.SourceAssetId, audio.AnalysisJobId,
                EntityId<JobIdTag>.New(), settings, Actor, At), Initial, SourceOnlyExecutionPhase.Completed);
            Register(Initial);
        }

        internal SubtitleEditStore Store { get; }
        internal SourceOnlyGeneratedRuntimeEvidence Initial { get; }
        internal StoredSourceOnlyExecution InitialExecution { get; }

        internal (StoredSourceOnlyExecution Execution, SourceOnlyGeneratedRuntimeEvidence Generated) Next(
            StoredSourceOnlyExecution previous, SourceOnlyGeneratedRuntimeEvidence generated,
            IReadOnlyList<SubtitleEditCapture> captures, bool? includeShort = null)
        {
            DateTimeOffset at = generated.CreatedAt.AddHours(1);
            var request = previous.Request with
            {
                ExecutionId = EntityId<SourceOnlyExecutionIdTag>.New(),
                RootJobId = EntityId<JobIdTag>.New(),
                ActorSid = Coordinator,
                CreatedAt = at,
                ParentVersionId = generated.Preparation.Child.Id,
                ParentDigest = generated.Preparation.Child.ContentDigest,
                SubtitleEdits = captures.Where(value => value.Sequence > 0)
                    .Select(value => new SubtitleEditHeadReference(value.RootVersionId, value.Sequence, value.Edits[^1].RecordDigest)).ToArray(),
            };
            return (Stored(request), Generate(generated.Preparation.Child, Coordinator, at.AddMinutes(1), includeShort ?? this.includeShort));
        }

        internal StoredSourceOnlyExecution Completed(StoredSourceOnlyExecution execution, SourceOnlyGeneratedRuntimeEvidence generated) =>
            Stored(execution.Request, generated, SourceOnlyExecutionPhase.Completed);

        internal static StoredSourceOnlyExecution Stored(SourceOnlyExecutionRequest request, SourceOnlyGeneratedRuntimeEvidence? generated = null,
            SourceOnlyExecutionPhase phase = SourceOnlyExecutionPhase.Queued)
        {
            // Correctly typed/bound selection evidence, deliberately with a QC failure.
            // No physical render/checkpoint or successful delivery is claimed here.
            SourceOnlyResult? result = generated is null ? null : new SourceOnlyResult(
                new SourceOnlyResultBinding(request.ProjectId, request.ExecutionId, request.RootJobId,
                    request.SettingsSnapshot.ContentDigest, generated.Preparation.Child.Id, generated.Preparation.Child.ContentDigest),
                StandardArtifactGenerationDisposition.Completed,
                [new SourceOnlyResultArtifact(SourceOnlyResultRole.LandscapeClean, null,
                    new SourceOnlyResultArtifactReference(SourceOnlyPipeline.StableId<ArtifactIdTag>(request.ExecutionId.ToString(), "selection-render"),
                        Hash("synthetic render"), 1, ArtifactKind.Render), generated.Preparation.Child.Id, generated.Preparation.Child.ContentDigest,
                    new SourceOnlyResultArtifactReference(EntityId<ArtifactIdTag>.From(request.SourceCandidateId.Value),
                        Hash("synthetic source"), 100, ArtifactKind.Original), Hash("synthetic graph"), Hash("synthetic manifest"),
                    [], null, null, SourceOnlyResultFailureCode.QualityControlFailed)], [],
                [new SourceOnlyResultFailure(SourceOnlyResultFailureCode.QualityControlFailed)], [], [], null, false);
            return new(request, "{\"syntheticSelectionFixture\":true}", Hash("selection fixture"), 0, null, null, 0, phase,
                result?.CanonicalJson, result?.CanonicalDigest, generated?.Preparation.Child.Id, generated?.Preparation.Child.ContentDigest,
                phase == SourceOnlyExecutionPhase.Failed ? "source_only.quality_control_failed" : null, Hash("selection state fixture"));
        }

        internal void Register(SourceOnlyGeneratedRuntimeEvidence generated)
        {
            foreach (var item in generated.Versions.Values)
            {
                projects.AppendCompositionVersion(item.Version, CompositionSnapshotJson.WriteVersion(item.Version));
                StandardShortFormPlan? shortForm = item.Version.Id == generated.Preparation.Child.Id ? null : generated.Preparation.Plan.ShortForms.Single();
                service.RegisterGenerated(SourceOnlyApplicationRegistrations.CreateLayout(generated, item, shortForm),
                    SourceOnlyApplicationRegistrations.GeneratedKey(generated, item), projection: item.SubtitleProjection);
            }
        }

        internal SubtitleEditCapture EditAndCapture(SourceOnlyGeneratedRuntimeEvidence generated,
            SourceOnlyGeneratedSubtitleRuntimeEvidence item, string text)
        {
            Assert.AreSame(item, generated.Versions[item.Version.Id]);
            LayoutSubtitleBridgeSnapshot head = service.GetLatest(project.Id, item.Version.Id);
            SubtitleCueHistory history = head.Subtitles[0].History;
            var request = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
            {
                projectId = project.Id.ToString(),
                expectedRevision = head.Revision,
                expectedVersionId = head.CompositionVersion.Id.ToString(),
                expectedVersionDigest = head.CompositionVersion.ContentDigest.Hex,
                operation = new
                {
                    kind = "editSubtitle",
                    cueId = history.CueId.ToString(),
                    expectedRevisionId = history.CurrentRevision.Id.ToString(),
                    expectedRevisionDigest = history.CurrentRevision.ContentDigest.Hex,
                    reason = "Reviewed fixture correction",
                    edit = new { kind = "text", text },
                },
            }));
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, service.Execute(request, Actor, CancellationToken.None).Status);
            StoredSubtitleEdit row = Store.ReadBranch(project.Id, item.Version.Id)[^1];
            return service.CapturePrefix(project.Id, item.Version.Id, row.Sequence, row.RecordDigest);
        }

        private SourceOnlyGeneratedRuntimeEvidence Generate(CompositionVersion parent, string actor, DateTimeOffset at, bool shortForm)
        {
            var copy = new WorkingCopy(EntityId<WorkingCopyIdTag>.New(), project.Id, parent.Id, at, at, parent.Elements);
            BundledRenderProfileDefinition landscape = StandardRenderProfileFactory.CreateLandscape(profile, settings,
                BundledRenderProfileLoader.Load(landscapeJson), new RationalNumber(30, 1));
            var template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), landscape.TemplateReference, BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
            LongFormElementInput Omitted(LongFormElementKind kind) => new(EntityId<LongFormElementIdTag>.New(), kind, false, null, null, [], []);
            var longForm = new LongFormCompositionRequest(EntityId<LongFormPlanIdTag>.New(), project,
                new LongFormProvenanceSnapshot(project.Id, import.Revision.Id, Hash(TranscriptSnapshotJson.WriteRevision(import.Revision)),
                    import.SourceSpans.Append(full).ToArray(), []), template, landscape, LongFormDestination.InternalDistribution, null,
                Omitted(LongFormElementKind.PreRollMargin), Omitted(LongFormElementKind.Opening), Omitted(LongFormElementKind.ProgramName),
                Omitted(LongFormElementKind.EpisodeNumber), Omitted(LongFormElementKind.EpisodeTitle), Omitted(LongFormElementKind.PerformerIntroduction),
                Omitted(LongFormElementKind.PublicationScopeIndicator), Omitted(LongFormElementKind.Body), [], Omitted(LongFormElementKind.Summary),
                [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omitted(LongFormElementKind.CallToAction))],
                Omitted(LongFormElementKind.Ending), Omitted(LongFormElementKind.PostRollMargin));
            TopicBoundaryScanResult scan = TopicBoundaryService.FindCandidates(import.Revision, import.SourceSpans, EntityId<ModelRunIdTag>.New());
            TopicRangeRevision range = TopicBoundaryService.CreateTopicRange(scan, EntityId<TopicRangeIdTag>.New(), EntityId<TopicRangeRevisionIdTag>.New(),
                null, null, import.Revision.Utterances.Single().Range, actor, at, "Synthetic topic range");
            TopicMetadataRevision metadata = TopicMetadataService.CreateInitial(project, import.Revision, import.SourceSpans, range,
                null, 1, EntityId<TopicMetadataIdTag>.New(), EntityId<TopicMetadataRevisionIdTag>.New(), actor, at, "Synthetic structural metadata");
            SourceSpan span = import.SourceSpans.Single();
            EmphasisSuggestionCandidate[] candidates = shortForm ? [SuggestionService.Register(new EmphasisCandidate(
                new Suggestion(EntityId<SuggestionIdTag>.New(), EntityId<ModelRunIdTag>.New(), SuggestionKind.Highlight, SuggestionStatus.Proposed,
                    OriginRef.FromSourceSpan(span.Id), "Select the fixture source span", null, null, null), span, import.Revision.Id,
                longForm.Provenance.TranscriptRevisionDigest, metadata.Id, EmphasisCandidateService.RuleVersion, import.Revision.Utterances.Single().Text,
                EmphasisTextSourceKind.OriginalTranscript, [EmphasisReasonKind.ImportantStatement], []), [])] : [];
            StandardCompositionPreparation preparation = StandardCompositionOrchestrator.Prepare(new StandardCompositionOrchestrationRequest(parent, copy, 0,
                new StandardProjectStoreState(0, Hash(HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project)), At), profile, settings,
                landscapeJson, verticalJson, longForm, EntityId<TimelineMapIdTag>.New(), new SourceDisplayGeometry(1920, 1080), new RationalNumber(30, 1),
                Hash("synthetic ffmpeg"), Hash("synthetic model"), new MusicPlanningService().Plan(
                    MusicCatalog.CreateUnavailable(MusicLibraryState.Absent, MusicCatalogWarning.LibraryAbsent), [], MusicPlanningOptions.Default),
                [metadata], candidates, [], actor, at, "Prepare pure projection integration fixture"));
            SourceOnlyGeneratedRuntimeEvidence result = SourceOnlyPipeline.BuildGeneratedRuntimeEvidence(preparation, import, profile, settings,
                new RenderFontEvidence("Noto Sans JP", Hash("synthetic font")), actor, at.AddSeconds(1));
            Assert.AreEqual(shortForm ? 1 : 0, result.ShortForms.Count);
            Assert.IsTrue(result.Versions.Values.All(item => item.Histories.Count > 0));
            return result;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) { directory = directory.Parent; }
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "HubRadio.SubtitleProjectionPrefix", Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "store.db");
            Assert.IsTrue(DatabaseMigrator.Migrate(DatabasePath, Path.Combine(directory, "backups")).SchemaVersion >= 12);
            var at = new DateTimeOffset(2038, 4, 5, 6, 7, 8, TimeSpan.Zero);
            var project = new Project(EntityId<ProjectIdTag>.New(), "Prefix fixture", "Historical capture", "Editors",
                PublicationScope.InternalOnly, null, [], []);
            var projects = new ProjectStore(DatabasePath);
            projects.CreateProject(project, HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project), at);
            var assetId = EntityId<AssetIdTag>.New();
            var element = new CompositionElement(EntityId<CompositionElementIdTag>.New(), Range(0, 4), OriginRef.FromAsset(assetId));
            var version = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), project.Id, null, Actor, at,
                "Generated prefix fixture", ContentDigest.Compute(CompositionSnapshotJson.WriteElementsObjectToUtf8([element])), [element]);
            projects.AppendCompositionVersion(version, CompositionSnapshotJson.WriteVersion(version));
            var transcriptId = EntityId<TranscriptRevisionIdTag>.New();
            var spanId = EntityId<SourceSpanIdTag>.New();
            var word = new Token(EntityId<TranscriptTokenIdTag>.New(), "Initial subtitle.", Range(0, 2), null);
            var utterance = new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), spanId, EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker", word.Text, Range(0, 2), null,
                [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), word.Text, [word])]);
            var transcript = new TranscriptRevision(transcriptId, null, TranscriptChangeSource.Machine, at, [utterance]);
            var span = new SourceSpan(spanId, assetId, "audio:0", Range(0, 2), Hash("fixture source"), transcriptId, [word.Id]);
            SubtitleCueCandidate candidate = SubtitleSegmenter.SegmentMachine(transcript,
                Hash(TranscriptSnapshotJson.WriteRevision(transcript)), [span]).Candidates.Single();
            var full = new NormalizedRect(default, default, new RationalNumber(1, 1), new RationalNumber(1, 1));
            var layout = new LayoutSpec(EntityId<LayoutSpecIdTag>.New(), 9, 16,
                [new LayoutRegion("subtitle", full, LayoutRegionRole.Subtitle)], [new SafeZone("output-frame-bounds", full)]);
            var profile = new SubtitleLayoutProfile(layout.Id, Hash("fixture profile"), ["output-frame-bounds"],
                Enum.GetValues<SubtitleSoftConstraintKind>().Select(kind => new SubtitleSoftConstraintWeight(kind, 0)).ToArray());
            var placement = new SubtitlePlacementCandidate(EntityId<SubtitlePlacementCandidateIdTag>.New(), "subtitle",
                new SubtitleLayoutRect(default, new RationalNumber(3, 4), new RationalNumber(1, 1), new RationalNumber(1, 4)));
            SubtitleLayoutResolution resolution = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(candidate, layout, profile, [placement], [], [], null));
            SubtitleCueHistory history = SubtitleService.CreateInitial(version, candidate, resolution,
                EntityId<CueIdTag>.New(), EntityId<SubtitleCueRevisionIdTag>.New(), Range(0, 2), Actor, at, "Generated initial subtitle");
            Initial = new LayoutSubtitleBridgeSnapshot(project.Id, 0, version, layout, 1080, 1920,
                [new LayoutSubtitleSourceBinding(assetId, OriginRef.FromAsset(assetId), new SourceDisplayGeometry(1080, 1920))], [],
                [new LayoutSubtitleCueBinding(history, layout, profile, [], [])], [],
                [new LayoutSubtitleDisplayBinding(new SubtitleDisplayProfile(profile.VersionDigest,
                    SubtitleCueDisplayMode.Full, SubtitlePunctuationDisplay.Preserve), [])]);
            Store = new SubtitleEditStore(DatabasePath);
            Service = new DurableSubtitleEditService(Store);
            Service.RegisterGenerated(Initial, InputDigest);
        }

        internal string DatabasePath { get; }
        internal SubtitleEditStore Store { get; }
        internal DurableSubtitleEditService Service { get; }
        internal LayoutSubtitleBridgeSnapshot Initial { get; }
        internal ContentDigest InputDigest { get; } = Hash("fixture generated input");
        internal IReadOnlyList<StoredSubtitleEdit> Rows => Store.ReadBranch(Initial.ProjectId, Initial.CompositionVersion.Id);

        internal LayoutSubtitleBridgeSnapshot Edit(LayoutSubtitleBridgeSnapshot parent, string text)
        {
            SubtitleCueHistory history = parent.Subtitles.Single().History;
            var request = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
            {
                projectId = parent.ProjectId.ToString(),
                expectedRevision = parent.Revision,
                expectedVersionId = parent.CompositionVersion.Id.ToString(),
                expectedVersionDigest = parent.CompositionVersion.ContentDigest.Hex,
                operation = new
                {
                    kind = "editSubtitle",
                    cueId = history.CueId.ToString(),
                    expectedRevisionId = history.CurrentRevision.Id.ToString(),
                    expectedRevisionDigest = history.CurrentRevision.ContentDigest.Hex,
                    reason = "Correct reviewed text",
                    edit = new { kind = "text", text },
                },
            }));
            LayoutSubtitleBridgeOperationResult result = Service.Execute(request, Actor, CancellationToken.None);
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, result.Status);
            return result.Snapshot ?? throw new AssertFailedException();
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}