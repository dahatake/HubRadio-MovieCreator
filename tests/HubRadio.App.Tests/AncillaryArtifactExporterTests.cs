using System.Reflection;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Delivery;
using HubRadio.App.Reviews;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AncillaryArtifactExporterTests
{
    [TestMethod]
    public async Task ExportAsync_PromotesSixCanonicalVersionBoundArtifacts()
    {
        using Fixture fixture = new();

        AncillaryArtifactExportResult result = await fixture.Exporter.ExportAsync(
            fixture.CreateRequest());

        Assert.IsFalse(result.WasAlreadyRegistered);
        Assert.AreEqual(fixture.ExportId, result.ExportId);
        Assert.HasCount(6, result.Artifacts);
        CollectionAssert.AreEqual(
            Enum.GetValues<AncillaryArtifactKind>(),
            result.Artifacts.Select(static artifact => artifact.Kind).ToArray());
        Assert.HasCount(6, result.DeliveryArtifactEvidence);
        Assert.HasCount(6, fixture.Store.ListProjectArtifacts(fixture.Project.Id));

        foreach (AncillaryArtifactOutput output in result.Artifacts)
        {
            byte[] bytes = fixture.ReadArtifactBytes(output.StoredArtifact);
            CollectionAssert.AreEqual(bytes, CanonicalJson.CanonicalizeToUtf8(bytes));
            Assert.IsFalse(HasUtf8Bom(bytes));
            Assert.AreEqual(bytes.LongLength, output.StoredArtifact.ByteSize);
            Assert.AreEqual(
                ContentDigest.Compute(bytes),
                output.StoredArtifact.ContentDigest);
            Assert.AreEqual(
                output.StoredArtifact.ContentDigest,
                output.ManifestArtifact.Sha256);
            Assert.AreEqual(
                output.StoredArtifact.StorageReference,
                output.ManifestArtifact.StorageReference);
            Assert.AreEqual("application/json", output.ManifestArtifact.MediaType);
            Assert.AreEqual("ja-JP", output.ManifestArtifact.Language);
            Assert.AreEqual(
                ArtifactKind.Delivery,
                output.StoredArtifact.Kind);
            Assert.AreEqual(fixture.CreatedAt, output.StoredArtifact.CreatedAt);

            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            Assert.AreEqual(
                AncillaryArtifactExporter.SchemaVersion,
                root.GetProperty("schemaVersion").GetString());
            Assert.AreEqual(
                fixture.ExportId.ToString(),
                root.GetProperty("exportId").GetString());
            Assert.AreEqual(
                fixture.Project.Id.ToString(),
                root.GetProperty("projectId").GetString());
            Assert.AreEqual(
                fixture.TargetVersion.Id.ToString(),
                root.GetProperty("compositionVersionId").GetString());
            Assert.AreEqual(
                fixture.TargetVersion.ContentDigest.Hex,
                root.GetProperty("compositionVersionDigest").GetString());
            Assert.AreEqual(
                result.ReviewEvidenceDigest.Hex,
                root.GetProperty("reviewEvidenceDigest").GetString());
            Assert.AreEqual("ja-JP", root.GetProperty("language").GetString());
            Assert.AreEqual(
                output.PayloadDigest.Hex,
                root.GetProperty("payloadDigest").GetString());
            Assert.AreEqual(
                output.PayloadDigest,
                ContentDigest.Compute(CanonicalJson.Write(
                    root.GetProperty("payload"))));
            Assert.IsGreaterThanOrEqualTo(
                4,
                root.GetProperty("origins").GetArrayLength());
            AssertPropertyOrderIsCanonical(root);
        }

        AncillaryArtifactOutput reviewOutput = GetOutput(
            result,
            AncillaryArtifactKind.ReviewEvidence);
        Assert.AreEqual(reviewOutput.PayloadDigest, result.ReviewEvidenceDigest);

        using JsonDocument transcript = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.Transcript));
        JsonElement transcriptPayload = transcript.RootElement.GetProperty("payload");
        Assert.AreEqual(
            fixture.Transcript.Id.ToString(),
            transcriptPayload.GetProperty("revision").GetProperty("id").GetString());
        Assert.AreEqual(2, transcriptPayload.GetProperty("sourceSpans").GetArrayLength());

        using JsonDocument chapters = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.Chapters));
        JsonElement chapter = chapters.RootElement.GetProperty("payload")
            .GetProperty("chapters")[0];
        Assert.AreEqual("Editorial chapter title", chapter.GetProperty("title").GetString());
        Assert.AreEqual(
            "manualEditorial",
            chapter.GetProperty("titleValueOrigin").GetString());
        Assert.AreEqual(
            fixture.ChapterEditorialRecordId.ToString(),
            chapter.GetProperty("origin")
                .GetProperty("editorialRecordId")
                .GetString());

        using JsonDocument publishing = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.PublishingText));
        JsonElement publishingPayload = publishing.RootElement.GetProperty("payload");
        Assert.AreEqual(
            "Editorial launch title",
            publishingPayload.GetProperty("title").GetProperty("value").GetString());
        CollectionAssert.AreEqual(
            new[] { "#Alpha", "#Zulu" },
            publishingPayload.GetProperty("hashtags")
                .EnumerateArray()
                .Select(static item => item.GetProperty("value").GetString())
                .ToArray());
        Assert.IsFalse(
            fixture.ReadArtifactText(
                    GetOutput(result, AncillaryArtifactKind.PublishingText))
                .Contains(fixture.Project.ContentName, StringComparison.Ordinal));

        using JsonDocument editing = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.EditingInstructions));
        JsonElement editingPayload = editing.RootElement.GetProperty("payload");
        Assert.AreEqual(
            fixture.SourceVersion.Id.ToString(),
            editingPayload.GetProperty("fromVersionId").GetString());
        Assert.AreEqual(
            fixture.TargetVersion.Id.ToString(),
            editingPayload.GetProperty("toVersionId").GetString());
        Assert.AreEqual(1, editingPayload.GetProperty("changes").GetArrayLength());
        Assert.AreEqual(
            "added",
            editingPayload.GetProperty("changes")[0].GetProperty("kind").GetString());

        using JsonDocument review = fixture.ReadArtifactDocument(reviewOutput);
        JsonElement reviewPayload = review.RootElement.GetProperty("payload");
        Assert.AreEqual(2, reviewPayload.GetProperty("reviewRecords").GetArrayLength());
        Assert.AreEqual(2, reviewPayload.GetProperty("reviewComments").GetArrayLength());
        Assert.AreEqual(2, reviewPayload.GetProperty("approvals").GetArrayLength());

        using JsonDocument ranges = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.UsedSourceRanges));
        JsonElement entries = ranges.RootElement.GetProperty("payload")
            .GetProperty("entries");
        Assert.AreEqual(1, entries.GetArrayLength());
        Assert.AreEqual(
            fixture.FirstSourceSpan.Id.ToString(),
            entries[0].GetProperty("sourceSpan").GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task ExportAsync_PreservesAbsentPublishingAndChaptersWithoutInventingText()
    {
        using Fixture fixture = new();
        EntityId<AncillaryArtifactExportIdTag> exportId =
            EntityId<AncillaryArtifactExportIdTag>.New();
        IReadOnlyDictionary<AncillaryArtifactKind, EntityId<ArtifactIdTag>> ids =
            Fixture.CreateArtifactIds();
        var publishing = new AncillaryPublishingText(
            title: null,
            summary: null,
            postText: null,
            hashtags: []);

        AncillaryArtifactExportResult result = await fixture.Exporter.ExportAsync(
            fixture.CreateRequest(
                exportId: exportId,
                chapters: [],
                publishingText: publishing,
                artifactIds: ids));

        using JsonDocument chapterDocument = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.Chapters));
        Assert.AreEqual(
            0,
            chapterDocument.RootElement.GetProperty("payload")
                .GetProperty("chapters")
                .GetArrayLength());

        using JsonDocument publishingDocument = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.PublishingText));
        JsonElement payload = publishingDocument.RootElement.GetProperty("payload");
        Assert.AreEqual(JsonValueKind.Null, payload.GetProperty("title").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, payload.GetProperty("summary").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, payload.GetProperty("postText").ValueKind);
        Assert.AreEqual(0, payload.GetProperty("hashtags").GetArrayLength());

        string serialized = publishingDocument.RootElement.GetRawText();
        Assert.IsFalse(serialized.Contains(fixture.Project.ContentName, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("Topic 1", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("generated", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExportAsync_WritesOnlyTheRequestedGroundedSubsetOfKinds()
    {
        using Fixture fixture = new();
        AncillaryArtifactKind[] requested =
        [
            AncillaryArtifactKind.Transcript,
            AncillaryArtifactKind.UsedSourceRanges,
        ];
        IReadOnlyDictionary<AncillaryArtifactKind, EntityId<ArtifactIdTag>> ids =
            requested.ToDictionary(
                static kind => kind,
                static _ => EntityId<ArtifactIdTag>.New());

        AncillaryArtifactExportResult result = await fixture.Exporter.ExportAsync(
            fixture.CreateRequest(artifactIds: ids, kinds: requested));

        Assert.HasCount(2, result.Artifacts);
        CollectionAssert.AreEqual(
            requested,
            result.Artifacts.Select(static artifact => artifact.Kind).ToArray());
        Assert.HasCount(2, result.DeliveryArtifactEvidence);
        Assert.HasCount(2, fixture.Store.ListProjectArtifacts(fixture.Project.Id));

        foreach (string excluded in new[]
        {
            "chapters.json",
            "publishing-text.json",
            "editing-instructions.json",
            "review-evidence.json",
        })
        {
            Assert.IsEmpty(
                Directory.GetFiles(
                    fixture.WorkspacePath,
                    excluded,
                    SearchOption.AllDirectories),
                "An ungrounded ancillary artifact must never reach the workspace.");
        }

        Assert.HasCount(
            2,
            Directory.GetFiles(
                fixture.WorkspacePath,
                "*.json",
                SearchOption.AllDirectories));

        // The review payload is still the envelope binding for every artifact
        // even though review evidence itself is not delivered.
        using JsonDocument transcript = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.Transcript));
        Assert.AreEqual(
            result.ReviewEvidenceDigest.Hex,
            transcript.RootElement.GetProperty("reviewEvidenceDigest").GetString());
    }

    [TestMethod]
    public async Task ExportAsync_RejectsArtifactIdsThatDoNotMatchTheRequestedKinds()
    {
        using Fixture fixture = new();
        AncillaryArtifactKind[] requested =
        [
            AncillaryArtifactKind.Transcript,
            AncillaryArtifactKind.UsedSourceRanges,
        ];

        _ = Assert.ThrowsExactly<ArgumentException>(() => fixture.CreateRequest(
            artifactIds: Fixture.CreateArtifactIds(),
            kinds: requested));
        _ = Assert.ThrowsExactly<ArgumentException>(() => fixture.CreateRequest(
            artifactIds: new Dictionary<AncillaryArtifactKind, EntityId<ArtifactIdTag>>
            {
                [AncillaryArtifactKind.Transcript] = EntityId<ArtifactIdTag>.New(),
            },
            kinds: requested));
        _ = Assert.ThrowsExactly<ArgumentException>(() => fixture.CreateRequest(
            kinds: []));
        _ = Assert.ThrowsExactly<ArgumentException>(() => fixture.CreateRequest(
            kinds:
            [
                AncillaryArtifactKind.Transcript,
                AncillaryArtifactKind.Transcript,
            ]));

        Assert.HasCount(0, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        await Task.CompletedTask;
    }

    /// <summary>
    /// Reproduces the actual source-only span universe: the composition element
    /// originates from a full-source envelope span that carries the transcript
    /// revision id but is not one of its utterance spans.
    /// </summary>
    [TestMethod]
    public async Task ExportAsync_AcceptsAFullSourceEnvelopeSpanThatTheCompositionOriginatesFrom()
    {
        using Fixture fixture = new();
        SourceSpan envelope = fixture.CreateFullSourceEnvelopeSpan();
        var element = new CompositionElement(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(default, envelope.Range.Duration),
            OriginRef.FromSourceSpan(envelope.Id),
            sourceRange: envelope.Range);
        var version = new CompositionVersion(
            fixture.TargetVersion.Id,
            fixture.Project.Id,
            parentVersionId: null,
            fixture.TargetVersion.CreatedByActorId,
            fixture.TargetVersion.CreatedAt,
            "Span the whole original source.",
            RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
            [element]);
        AncillaryArtifactKind[] requested =
        [
            AncillaryArtifactKind.Transcript,
            AncillaryArtifactKind.UsedSourceRanges,
        ];

        AncillaryArtifactExportResult result = await fixture.Exporter.ExportAsync(
            fixture.CreateRequest(
                compositionVersion: version,
                sourceSpans: [.. fixture.SourceSpans, envelope],
                chapters: [],
                publishingText: new AncillaryPublishingText(null, null, null, []),
                omitEditingSourceVersion: true,
                reviewEvidence: fixture.CreateEmptyReviewEvidence(version),
                editorialRecordIds: [],
                artifactIds: requested.ToDictionary(
                    static kind => kind,
                    static _ => EntityId<ArtifactIdTag>.New()),
                kinds: requested));

        Assert.HasCount(2, result.Artifacts);
        using JsonDocument ranges = fixture.ReadArtifactDocument(
            GetOutput(result, AncillaryArtifactKind.UsedSourceRanges));
        JsonElement entries = ranges.RootElement.GetProperty("payload").GetProperty("entries");
        Assert.AreEqual(1, entries.GetArrayLength());
        Assert.AreEqual(
            envelope.Id.ToString(),
            entries[0].GetProperty("sourceSpan").GetProperty("id").GetString());
    }

    /// <summary>
    /// Reproduces a derived candidate span: its range is a SUB-range of the
    /// utterance it came from, which is what the emphasis/editorial detectors
    /// produce and what the source-only composition carries.
    /// </summary>
    [TestMethod]
    public async Task ExportAsync_AcceptsACandidateSpanThatCoversOnlyPartOfItsUtterance()
    {
        using Fixture fixture = new();
        SourceSpan candidate = fixture.CreateFirstTokenCandidateSpan();
        Assert.IsTrue(candidate.Range.Start >= fixture.FirstSourceSpan.Range.Start &&
            candidate.Range.End <= fixture.FirstSourceSpan.Range.End,
            "The candidate must be inside its utterance, not around it.");
        AncillaryArtifactKind[] requested =
        [
            AncillaryArtifactKind.Transcript,
            AncillaryArtifactKind.UsedSourceRanges,
        ];

        AncillaryArtifactExportResult result = await fixture.Exporter.ExportAsync(
            fixture.CreateRequest(
                sourceSpans: [.. fixture.SourceSpans, candidate],
                chapters: [],
                publishingText: new AncillaryPublishingText(null, null, null, []),
                editorialRecordIds: [],
                artifactIds: requested.ToDictionary(
                    static kind => kind,
                    static _ => EntityId<ArtifactIdTag>.New()),
                kinds: requested));

        Assert.HasCount(2, result.Artifacts);
    }

    [TestMethod]
    public async Task ExportAsync_StillRejectsASpanThatInventsTranscriptProvenance()
    {
        using Fixture fixture = new();
        var forged = new SourceSpan(
            EntityId<SourceSpanIdTag>.New(),
            fixture.Asset.Id,
            "audio:1",
            fixture.CreateFullSourceEnvelopeSpan().Range,
            fixture.Asset.ContentDigest,
            fixture.Transcript.Id,
            [EntityId<TranscriptTokenIdTag>.New()]);

        AncillaryArtifactExportException exception =
            await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    sourceSpans: [.. fixture.SourceSpans, forged])));

        Assert.AreEqual(
            AncillaryArtifactExportFailureReason.TranscriptBindingMismatch,
            exception.Reason);
        Assert.HasCount(0, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task ExportAsync_IsOrderIndependentAndExactRerunIsIdempotent()
    {
        using Fixture fixture = new();

        AncillaryArtifactExportResult first = await fixture.Exporter.ExportAsync(
            fixture.CreateRequest());
        AncillaryArtifactExportResult second = await fixture.Exporter.ExportAsync(
            fixture.CreateReverseOrderedRequest());

        Assert.IsFalse(first.WasAlreadyRegistered);
        Assert.IsTrue(second.WasAlreadyRegistered);
        Assert.AreEqual(first.ReviewEvidenceDigest, second.ReviewEvidenceDigest);
        CollectionAssert.AreEqual(
            first.Artifacts.Select(static item => item.StoredArtifact.ContentDigest)
                .ToArray(),
            second.Artifacts.Select(static item => item.StoredArtifact.ContentDigest)
                .ToArray());
        CollectionAssert.AreEqual(
            first.Artifacts.Select(static item => item.PayloadDigest).ToArray(),
            second.Artifacts.Select(static item => item.PayloadDigest).ToArray());
        Assert.HasCount(6, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task ExportAsync_RejectsPlaceholderChapterWithoutCreatingArtifacts()
    {
        using Fixture fixture = new();
        var placeholder = new AncillaryChapter(
            fixture.PlaceholderTopic,
            OriginRef.FromEditorialRecord(fixture.ChapterEditorialRecordId));

        AncillaryArtifactExportException exception =
            await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    chapters: [placeholder])));

        Assert.AreEqual(
            AncillaryArtifactExportFailureReason.ChapterEvidenceInvalid,
            exception.Reason);
        Assert.IsFalse(exception.ReconciliationRequired);
        Assert.HasCount(0, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(fixture.ExportId)));
    }

    [TestMethod]
    public async Task ExportAsync_RejectsBrokenContextTranscriptEditingReviewAndOrigins()
    {
        using (Fixture fixture = new())
        {
            EntityId<ProjectIdTag> otherProject = EntityId<ProjectIdTag>.New();
            var crossProject = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                otherProject,
                fixture.SourceVersion.Id,
                Fixture.EditorSid,
                fixture.CreatedAt,
                "Cross-project candidate.",
                fixture.TargetVersion.ContentDigest,
                fixture.TargetVersion.Elements);
            await AssertFailureAsync(
                () => fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    compositionVersion: crossProject)),
                AncillaryArtifactExportFailureReason.ContextMismatch);
        }

        using (Fixture fixture = new())
        {
            SourceSpan span = fixture.FirstSourceSpan;
            var broken = new SourceSpan(
                span.Id,
                span.AssetId,
                span.StreamId,
                span.Range,
                Digest('f'),
                span.TranscriptRevisionId,
                span.TranscriptTokenIds);
            await AssertFailureAsync(
                () => fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    sourceSpans: [broken, fixture.SecondSourceSpan])),
                AncillaryArtifactExportFailureReason.TranscriptBindingMismatch);
        }

        using (Fixture fixture = new())
        {
            await AssertFailureAsync(
                () => fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    omitEditingSourceVersion: true)),
                AncillaryArtifactExportFailureReason.EditingEvidenceInvalid);
        }

        using (Fixture fixture = new())
        {
            var brokenReview = new StoredVersionEvidence(
                fixture.TargetVersion.Id,
                fixture.Project.Id,
                Digest('f'),
                fixture.ReviewEvidence.ReviewRecords,
                fixture.ReviewEvidence.ReviewComments,
                fixture.ReviewEvidence.Approvals);
            await AssertFailureAsync(
                () => fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    reviewEvidence: brokenReview)),
                AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
        }

        using (Fixture fixture = new())
        {
            var brokenPublishing = new AncillaryPublishingText(
                new AncillaryAttributedText(
                    "Unbound title",
                    OriginRef.FromEditorialRecord(
                        EntityId<EditorialRecordIdTag>.New())),
                summary: null,
                postText: null,
                hashtags: []);
            await AssertFailureAsync(
                () => fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    publishingText: brokenPublishing)),
                AncillaryArtifactExportFailureReason.ProvenanceInvalid);
        }

        using (Fixture fixture = new())
        {
            var outsideRangeElement = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                new TimeRange(default, new RationalTime(2, 1)),
                OriginRef.FromSourceSpan(fixture.FirstSourceSpan.Id),
                sourceRange: new TimeRange(
                    new RationalTime(1, 1),
                    new RationalTime(2, 1)));
            var outsideRangeVersion = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                fixture.Project.Id,
                fixture.SourceVersion.Id,
                Fixture.EditorSid,
                fixture.CreatedAt,
                "Retain an invalid source range candidate.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    [outsideRangeElement]),
                [outsideRangeElement]);
            var matchingEmptyReview = new StoredVersionEvidence(
                outsideRangeVersion.Id,
                fixture.Project.Id,
                outsideRangeVersion.ContentDigest,
                reviewRecords: [],
                reviewComments: [],
                approvals: []);
            await AssertFailureAsync(
                () => fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    compositionVersion: outsideRangeVersion,
                    reviewEvidence: matchingEmptyReview)),
                AncillaryArtifactExportFailureReason.ProvenanceInvalid);
        }

        using (Fixture fixture = new())
        {
            var mismatchedComment = new StoredReviewComment(
                EntityId<ReviewCommentIdTag>.New(),
                fixture.Project.Id,
                fixture.TargetVersion.Id,
                fixture.TargetVersion.ContentDigest,
                new RationalTime(1, 1),
                fixture.SecondSourceSpan.Id,
                Fixture.EditorSid,
                ReviewActorRole.Editor,
                fixture.CreatedAt,
                "This span is not used at the comment output time.");
            var brokenReview = new StoredVersionEvidence(
                fixture.TargetVersion.Id,
                fixture.Project.Id,
                fixture.TargetVersion.ContentDigest,
                fixture.ReviewEvidence.ReviewRecords,
                [mismatchedComment],
                fixture.ReviewEvidence.Approvals);
            await AssertFailureAsync(
                () => fixture.Exporter.ExportAsync(fixture.CreateRequest(
                    reviewEvidence: brokenReview)),
                AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
        }
    }

    [TestMethod]
    public async Task ExportAsync_PreCanceledRequestHasNoDurableOrStagingOutput()
    {
        using Fixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.Exporter.ExportAsync(
                    fixture.CreateRequest(),
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.HasCount(0, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(fixture.ExportId)));
    }

    [TestMethod]
    public async Task ExportAsync_CancellationDuringFirstPromotionRemainsCancellation()
    {
        using Fixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        int promotionCount = 0;
        var exporter = new AncillaryArtifactExporter(
            fixture.Store.GetArtifact,
            fixture.Store.ListProjectArtifacts,
            (_, _) =>
            {
                promotionCount++;
                cancellation.Cancel();
                return Task.FromException<StoredArtifact>(
                    new OperationCanceledException(cancellation.Token));
            },
            fixture.Store.ValidateRegisteredAsync,
            fixture.WorkspacePath);

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                exporter.ExportAsync(
                    fixture.CreateRequest(),
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(1, promotionCount);
        Assert.HasCount(0, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(fixture.ExportId)));
    }

    [TestMethod]
    public async Task ExportAsync_CancellationAfterFirstPromotionCompletesCommitBarrier()
    {
        using Fixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        int promotionCount = 0;
        var exporter = new AncillaryArtifactExporter(
            fixture.Store.GetArtifact,
            fixture.Store.ListProjectArtifacts,
            async (request, token) =>
            {
                StoredArtifact stored = await fixture.Store.PromoteAsync(
                    request,
                    token);
                promotionCount++;
                if (promotionCount == 1)
                {
                    cancellation.Cancel();
                }

                return stored;
            },
            fixture.Store.ValidateRegisteredAsync,
            fixture.WorkspacePath);

        AncillaryArtifactExportResult result = await exporter.ExportAsync(
            fixture.CreateRequest(),
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(6, promotionCount);
        Assert.HasCount(6, result.Artifacts);
        Assert.HasCount(6, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(fixture.ExportId)));
    }

    [TestMethod]
    public async Task ExportAsync_PartialPromotionRequiresReconciliationAndCleansOwnedStaging()
    {
        using Fixture fixture = new();
        int promotionCount = 0;
        var exporter = new AncillaryArtifactExporter(
            fixture.Store.GetArtifact,
            fixture.Store.ListProjectArtifacts,
            (request, token) =>
            {
                promotionCount++;
                return promotionCount == 2
                    ? Task.FromException<StoredArtifact>(
                        new ArtifactStoreException(
                            ArtifactStoreFailureReason.DatabaseRejected,
                            reconciliationRequired: false))
                    : fixture.Store.PromoteAsync(request, token);
            },
            fixture.Store.ValidateRegisteredAsync,
            fixture.WorkspacePath);

        AncillaryArtifactExportException exception =
            await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                exporter.ExportAsync(fixture.CreateRequest()));

        Assert.AreEqual(
            AncillaryArtifactExportFailureReason.PromotionFailed,
            exception.Reason);
        Assert.IsTrue(exception.ReconciliationRequired);
        Assert.AreEqual(2, promotionCount);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(fixture.ExportId)));
    }

    [TestMethod]
    public async Task ExportAsync_SixthPromotionCommittedThenThrowsRecoversExactSet()
    {
        using Fixture fixture = new();
        int promotionCount = 0;
        var exporter = new AncillaryArtifactExporter(
            fixture.Store.GetArtifact,
            fixture.Store.ListProjectArtifacts,
            async (request, token) =>
            {
                StoredArtifact stored = await fixture.Store.PromoteAsync(
                    request,
                    token);
                promotionCount++;
                if (promotionCount == 6)
                {
                    throw new IOException("The committed response was lost.");
                }

                return stored;
            },
            fixture.Store.ValidateRegisteredAsync,
            fixture.WorkspacePath);

        AncillaryArtifactExportResult result = await exporter.ExportAsync(
            fixture.CreateRequest());

        Assert.AreEqual(6, promotionCount);
        Assert.IsTrue(result.WasAlreadyRegistered);
        Assert.HasCount(6, result.Artifacts);
        Assert.HasCount(6, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(fixture.ExportId)));
    }

    [TestMethod]
    public async Task ExportAsync_PromotedMetadataDriftAndPrivateFailuresAreNormalized()
    {
        using (Fixture fixture = new())
        {
            var exporter = new AncillaryArtifactExporter(
                fixture.Store.GetArtifact,
                fixture.Store.ListProjectArtifacts,
                async (request, token) =>
                {
                    StoredArtifact stored = await fixture.Store.PromoteAsync(
                        request,
                        token);
                    return new StoredArtifact(
                        stored.Id,
                        stored.ProjectId,
                        stored.Kind,
                        stored.StorageReference,
                        stored.ContentDigest,
                        stored.ByteSize,
                        stored.CreatedAt.AddTicks(1));
                },
                fixture.Store.ValidateRegisteredAsync,
                fixture.WorkspacePath);

            AncillaryArtifactExportException drift =
                await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                    exporter.ExportAsync(fixture.CreateRequest()));

            Assert.AreEqual(
                AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                drift.Reason);
            Assert.IsTrue(drift.ReconciliationRequired);
            Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
            Assert.IsFalse(Directory.Exists(
                fixture.GetStagingDirectory(fixture.ExportId)));
        }

        using (Fixture fixture = new())
        {
            const string privateFailure =
                @"C:\private\transcript-body Bearer-secret";
            var exporter = new AncillaryArtifactExporter(
                fixture.Store.GetArtifact,
                fixture.Store.ListProjectArtifacts,
                (_, _) => Task.FromException<StoredArtifact>(
                    new IOException(privateFailure)),
                fixture.Store.ValidateRegisteredAsync,
                fixture.WorkspacePath);

            AncillaryArtifactExportException normalized =
                await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                    exporter.ExportAsync(fixture.CreateRequest()));

            Assert.AreEqual(
                AncillaryArtifactExportFailureReason.PromotionFailed,
                normalized.Reason);
            Assert.IsTrue(normalized.ReconciliationRequired);
            Assert.IsNull(normalized.InnerException);
            Assert.IsFalse(normalized.ToString().Contains(
                privateFailure,
                StringComparison.Ordinal));
            Assert.IsFalse(normalized.ToString().Contains(
                "Bearer-secret",
                StringComparison.OrdinalIgnoreCase));
            Assert.HasCount(0, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        }
    }

    [TestMethod]
    public async Task ExportAsync_RejectsRegisteredConflictsAndTamperedBytes()
    {
        using (Fixture fixture = new())
        {
            await fixture.RegisterConflictingArtifactAsync(
                fixture.ArtifactIds[AncillaryArtifactKind.Transcript]);

            AncillaryArtifactExportException conflict =
                await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                    fixture.Exporter.ExportAsync(fixture.CreateRequest()));

            Assert.AreEqual(
                AncillaryArtifactExportFailureReason.ArtifactConflict,
                conflict.Reason);
            Assert.IsTrue(conflict.ReconciliationRequired);
            Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        }

        using (Fixture fixture = new())
        {
            await fixture.RegisterPathConflictingArtifactAsync();

            AncillaryArtifactExportException pathConflict =
                await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                    fixture.Exporter.ExportAsync(fixture.CreateRequest()));

            Assert.AreEqual(
                AncillaryArtifactExportFailureReason.ArtifactConflict,
                pathConflict.Reason);
            Assert.IsTrue(pathConflict.ReconciliationRequired);
            Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        }

        using (Fixture fixture = new())
        {
            AncillaryArtifactExportResult result = await fixture.Exporter.ExportAsync(
                fixture.CreateRequest());
            AncillaryArtifactOutput transcript = GetOutput(
                result,
                AncillaryArtifactKind.Transcript);
            File.WriteAllBytes(
                fixture.GetArtifactPath(transcript.StoredArtifact),
                Encoding.UTF8.GetBytes("{}"));

            AncillaryArtifactExportException tampered =
                await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                    fixture.Exporter.ExportAsync(fixture.CreateRequest()));

            Assert.AreEqual(
                AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                tampered.Reason);
            Assert.IsTrue(tampered.ReconciliationRequired);
            Assert.IsNull(tampered.InnerException);
        }
    }

    [TestMethod]
    public async Task ExportAsync_DoesNotDeleteUnownedStagingConflict()
    {
        using Fixture fixture = new();
        string staging = fixture.GetStagingDirectory(fixture.ExportId);
        Directory.CreateDirectory(staging);
        string marker = Path.Combine(staging, "foreign-owner.txt");
        File.WriteAllText(marker, "foreign owner", new UTF8Encoding(false, true));

        AncillaryArtifactExportException exception =
            await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(() =>
                fixture.Exporter.ExportAsync(fixture.CreateRequest()));

        Assert.AreEqual(
            AncillaryArtifactExportFailureReason.StagingConflict,
            exception.Reason);
        Assert.IsFalse(exception.ReconciliationRequired);
        Assert.IsTrue(File.Exists(marker));
        Assert.AreEqual("foreign owner", File.ReadAllText(marker, Encoding.UTF8));
        Assert.HasCount(0, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task ExportedEvidenceCanBeRegisteredDirectlyByDeliveryService()
    {
        using Fixture fixture = new();
        AncillaryArtifactExportResult ancillary = await fixture.Exporter.ExportAsync(
            fixture.CreateRequest());

        DeliveryCreationResult delivery = await fixture.CreateDeliveryAsync(ancillary);

        Assert.AreEqual(fixture.Project.Id, delivery.Manifest.ProjectId);
        Assert.AreEqual(
            fixture.TargetVersion.Id,
            delivery.Manifest.CompositionVersionId);
        Assert.HasCount(7, delivery.Manifest.Artifacts);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "video",
                "transcript",
                "chapters",
                "publishing-text",
                "editing-instructions",
                "review-evidence",
                "used-source-ranges",
            },
            delivery.Manifest.Artifacts
                .Select(static artifact => artifact.ArtifactType)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { fixture.FirstSourceSpan.Id },
            delivery.UsedSourceSpans.Select(static span => span.Id).ToArray());
    }

    [TestMethod]
    public void InputsResultsAndFailuresAreImmutableStrictAndPrivate()
    {
        using Fixture fixture = new();
        OriginRef origin = OriginRef.FromEditorialRecord(
            fixture.PublishingEditorialRecordId);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AncillaryAttributedText("invalid\uD800", origin));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AncillaryPublishingText(
                title: null,
                summary: null,
                postText: null,
                [
                    new AncillaryAttributedText("#same", origin),
                    new AncillaryAttributedText("#same", origin),
                ]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.CreateRequest(language: "ja--JP"));

        EntityId<ArtifactIdTag> duplicateId = EntityId<ArtifactIdTag>.New();
        Dictionary<AncillaryArtifactKind, EntityId<ArtifactIdTag>> duplicates =
            Enum.GetValues<AncillaryArtifactKind>()
                .ToDictionary(static kind => kind, _ => duplicateId);
        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.CreateRequest(artifactIds: duplicates));

        var spans = fixture.SourceSpans.ToList();
        var chapters = fixture.Chapters.ToList();
        var editorialIds = fixture.EditorialRecordIds.ToList();
        var artifactIds = fixture.ArtifactIds.ToDictionary();
        AncillaryArtifactExportRequest request = fixture.CreateRequest(
            sourceSpans: spans,
            chapters: chapters,
            editorialRecordIds: editorialIds,
            artifactIds: artifactIds);
        spans.Clear();
        chapters.Clear();
        editorialIds.Clear();
        artifactIds.Clear();
        Assert.HasCount(2, request.SourceSpans);
        Assert.HasCount(1, request.Chapters);
        Assert.HasCount(2, request.EditorialRecordIds);
        Assert.HasCount(6, request.ArtifactIds);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SourceSpan>)request.SourceSpans).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<AncillaryArtifactKind, EntityId<ArtifactIdTag>>)
                request.ArtifactIds).Clear());

        foreach (Type type in new[]
        {
            typeof(AncillaryAttributedText),
            typeof(AncillaryPublishingText),
            typeof(AncillaryChapter),
            typeof(AncillaryArtifactExportRequest),
            typeof(AncillaryArtifactOutput),
            typeof(AncillaryArtifactExportResult),
            typeof(AncillaryArtifactExportException),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(AncillaryArtifactOutput).GetConstructors().Length);
        Assert.AreEqual(0, typeof(AncillaryArtifactExportResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(AncillaryArtifactExportException).GetConstructors().Length);
        Assert.AreEqual(
            1,
            typeof(AncillaryArtifactExporter).GetConstructors().Length);
        CollectionAssert.AreEqual(
            new[] { nameof(AncillaryArtifactExporter.ExportAsync) },
            typeof(AncillaryArtifactExporter).GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsFalse(typeof(AncillaryArtifactExporter).GetMethods()
            .Any(static method =>
                method.Name.Contains("Generate", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Infer", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Summarize", StringComparison.OrdinalIgnoreCase)));

        ConstructorInfo exceptionConstructor =
            typeof(AncillaryArtifactExportException)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single();
        foreach (AncillaryArtifactExportFailureReason reason in
            Enum.GetValues<AncillaryArtifactExportFailureReason>())
        {
            var exception = (AncillaryArtifactExportException)
                exceptionConstructor.Invoke([reason, false]);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.ToString().Contains(
                @"C:\private\transcript-body",
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task AssertFailureAsync(
        Func<Task<AncillaryArtifactExportResult>> action,
        AncillaryArtifactExportFailureReason reason)
    {
        AncillaryArtifactExportException exception =
            await Assert.ThrowsExactlyAsync<AncillaryArtifactExportException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsFalse(exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
    }

    private static AncillaryArtifactOutput GetOutput(
        AncillaryArtifactExportResult result,
        AncillaryArtifactKind kind) =>
        result.Artifacts.Single(artifact => artifact.Kind == kind);

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 &&
        bytes[0] == 0xef &&
        bytes[1] == 0xbb &&
        bytes[2] == 0xbf;

    private static void AssertPropertyOrderIsCanonical(JsonElement value)
    {
        string[] actual = value.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        string[] ordered = actual.OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(ordered, actual);
    }

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    private sealed class Fixture : IDisposable
    {
        internal const string EditorSid =
            "S-1-5-21-1000-2000-3000-4001";

        internal Fixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.AncillaryArtifactExporterTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            DatabasePath = Path.Combine(WorkspacePath, "hubradio.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));

            CreatedAt = new DateTimeOffset(
                2049,
                5,
                6,
                7,
                8,
                9,
                TimeSpan.Zero);
            Asset = new Asset(
                EntityId<AssetIdTag>.New(),
                "private-source.mov",
                "video/quicktime",
                Digest('a'),
                4096,
                new RightsRecord(
                    RightsStatus.Approved,
                    "rights-evidence",
                    "Approved for test delivery.",
                    externalPublicationAllowed: true));
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "Forbidden project fallback title",
                "Verify evidence-only ancillary export.",
                "Internal test reviewers",
                PublicationScope.ExternalPublicationApproved,
                deadline: null,
                [Asset],
                performers: []);
            new ProjectStore(DatabasePath).CreateProject(
                Project,
                "{\"project\":true}",
                CreatedAt.AddDays(-1));
            Store = new ArtifactStore(DatabasePath, WorkspacePath);

            EntityId<TranscriptRevisionIdTag> transcriptId =
                EntityId<TranscriptRevisionIdTag>.New();
            EntityId<TranscriptTokenIdTag> firstTokenId =
                EntityId<TranscriptTokenIdTag>.New();
            EntityId<TranscriptTokenIdTag> secondTokenId =
                EntityId<TranscriptTokenIdTag>.New();
            TimeRange firstRange = Range(0, 2);
            TimeRange secondRange = Range(2, 2);
            FirstSourceSpan = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                Asset.Id,
                "audio:1",
                firstRange,
                Asset.ContentDigest,
                transcriptId,
                [firstTokenId]);
            SecondSourceSpan = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                Asset.Id,
                "audio:1",
                secondRange,
                Asset.ContentDigest,
                transcriptId,
                [secondTokenId]);
            EntityId<TranscriptSpeakerIdTag> speakerId =
                EntityId<TranscriptSpeakerIdTag>.New();
            var firstToken = new Token(
                firstTokenId,
                "First exact statement",
                firstRange,
                recognitionConfidence: 0.95);
            var secondToken = new Token(
                secondTokenId,
                "Second exact statement",
                secondRange,
                recognitionConfidence: 0.85);
            var firstSentence = new Sentence(
                EntityId<TranscriptSentenceIdTag>.New(),
                firstToken.Text,
                [firstToken]);
            var secondSentence = new Sentence(
                EntityId<TranscriptSentenceIdTag>.New(),
                secondToken.Text,
                [secondToken]);
            var firstUtterance = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                FirstSourceSpan.Id,
                speakerId,
                "Speaker 1",
                firstToken.Text,
                firstRange,
                recognitionConfidence: 0.95,
                [firstSentence]);
            var secondUtterance = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                SecondSourceSpan.Id,
                speakerId,
                "Speaker 1",
                secondToken.Text,
                secondRange,
                recognitionConfidence: 0.85,
                [secondSentence]);
            Transcript = new TranscriptRevision(
                transcriptId,
                parentRevisionId: null,
                TranscriptChangeSource.Human,
                CreatedAt.AddHours(-2),
                [firstUtterance, secondUtterance]);
            SourceSpans = [FirstSourceSpan, SecondSourceSpan];

            TopicBoundaryScanResult boundaryScan =
                TopicBoundaryService.FindCandidates(
                    Transcript,
                    SourceSpans,
                    EntityId<HubRadio.Core.Analysis.ModelRunIdTag>.New());
            TopicRangeRevision topicRange = TopicBoundaryService.CreateTopicRange(
                boundaryScan,
                EntityId<TopicRangeIdTag>.New(),
                EntityId<TopicRangeRevisionIdTag>.New(),
                startBoundarySuggestionId: null,
                endBoundarySuggestionId: null,
                Range(0, 4),
                EditorSid,
                CreatedAt.AddMinutes(-30),
                "Define the exact chapter range.");
            PlaceholderTopic = TopicMetadataService.CreateInitial(
                Project,
                Transcript,
                SourceSpans,
                topicRange,
                speakerAssignments: null,
                topicOrdinal: 1,
                EntityId<TopicMetadataIdTag>.New(),
                EntityId<TopicMetadataRevisionIdTag>.New(),
                EditorSid,
                CreatedAt.AddMinutes(-20),
                "Create manual-first topic metadata.");
            ManualTopic = TopicMetadataService.SetTitle(
                Project,
                Transcript,
                SourceSpans,
                topicRange,
                speakerAssignments: null,
                PlaceholderTopic,
                EntityId<TopicMetadataRevisionIdTag>.New(),
                "Editorial chapter title",
                EditorSid,
                CreatedAt.AddMinutes(-10),
                "Confirm the editorial chapter title.");

            var targetElement = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                firstRange,
                OriginRef.FromSourceSpan(FirstSourceSpan.Id),
                sourceRange: firstRange);
            SourceVersion = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                parentVersionId: null,
                EditorSid,
                CreatedAt.AddHours(-1),
                "Create an empty source composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([]),
                elements: []);
            TargetVersion = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                SourceVersion.Id,
                EditorSid,
                CreatedAt.AddMinutes(-5),
                "Add the selected source range.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([targetElement]),
                [targetElement]);

            ChapterEditorialRecordId = EntityId<EditorialRecordIdTag>.New();
            PublishingEditorialRecordId = EntityId<EditorialRecordIdTag>.New();
            Chapters =
            [
                new AncillaryChapter(
                    ManualTopic,
                    OriginRef.FromEditorialRecord(ChapterEditorialRecordId)),
            ];
            EditorialRecordIds =
            [
                ChapterEditorialRecordId,
                PublishingEditorialRecordId,
            ];
            PublishingText = new AncillaryPublishingText(
                new AncillaryAttributedText(
                    "Editorial launch title",
                    OriginRef.FromEditorialRecord(PublishingEditorialRecordId)),
                new AncillaryAttributedText(
                    "Editorial summary text.",
                    OriginRef.FromSourceSpan(FirstSourceSpan.Id)),
                new AncillaryAttributedText(
                    "Editorial post text.",
                    OriginRef.FromEditorialRecord(PublishingEditorialRecordId)),
                [
                    new AncillaryAttributedText(
                        "#Zulu",
                        OriginRef.FromAsset(Asset.Id)),
                    new AncillaryAttributedText(
                        "#Alpha",
                        OriginRef.FromEditorialRecord(
                            PublishingEditorialRecordId)),
                ]);

            var firstRecord = new StoredReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                Project.Id,
                TargetVersion.Id,
                TargetVersion.ContentDigest,
                ReviewState.EditorReview,
                EditorSid,
                ReviewActorRole.Editor,
                CreatedAt.AddMinutes(1),
                "Submit the exact version for review.");
            var secondRecord = new StoredReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                Project.Id,
                TargetVersion.Id,
                TargetVersion.ContentDigest,
                ReviewState.Approved,
                "S-1-5-21-1000-2000-3000-4002",
                ReviewActorRole.ContentApprover,
                CreatedAt.AddMinutes(4),
                "Approve the exact reviewed version.");
            var firstComment = new StoredReviewComment(
                EntityId<ReviewCommentIdTag>.New(),
                Project.Id,
                TargetVersion.Id,
                TargetVersion.ContentDigest,
                new RationalTime(1, 2),
                FirstSourceSpan.Id,
                "S-1-5-21-1000-2000-3000-4003",
                ReviewActorRole.PerformerReviewer,
                CreatedAt.AddMinutes(2),
                "Confirm the first exact source statement.");
            var secondComment = new StoredReviewComment(
                EntityId<ReviewCommentIdTag>.New(),
                Project.Id,
                TargetVersion.Id,
                TargetVersion.ContentDigest,
                new RationalTime(3, 2),
                FirstSourceSpan.Id,
                "S-1-5-21-1000-2000-3000-4004",
                ReviewActorRole.Editor,
                CreatedAt.AddMinutes(3),
                "Retain the selected source range.");
            var contentApproval = new StoredApproval(
                EntityId<ApprovalIdTag>.New(),
                Project.Id,
                TargetVersion.Id,
                TargetVersion.ContentDigest,
                "S-1-5-21-1000-2000-3000-4002",
                ReviewActorRole.ContentApprover,
                CreatedAt.AddMinutes(5),
                "Approve the exact content version.");
            var brandApproval = new StoredApproval(
                EntityId<ApprovalIdTag>.New(),
                Project.Id,
                TargetVersion.Id,
                TargetVersion.ContentDigest,
                "S-1-5-21-1000-2000-3000-4005",
                ReviewActorRole.BrandApprover,
                CreatedAt.AddMinutes(6),
                "Approve the exact brand version.");
            ReviewEvidence = new StoredVersionEvidence(
                TargetVersion.Id,
                Project.Id,
                TargetVersion.ContentDigest,
                [secondRecord, firstRecord],
                [secondComment, firstComment],
                [brandApproval, contentApproval]);

            ExportId = EntityId<AncillaryArtifactExportIdTag>.New();
            ArtifactIds = CreateArtifactIds();
            Exporter = new AncillaryArtifactExporter(Store, WorkspacePath);
        }

        private string RootPath { get; }

        private string DatabasePath { get; }

        internal string WorkspacePath { get; }

        internal DateTimeOffset CreatedAt { get; }

        internal Asset Asset { get; }

        internal Project Project { get; }

        internal TranscriptRevision Transcript { get; }

        internal SourceSpan FirstSourceSpan { get; }

        internal SourceSpan SecondSourceSpan { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal TopicMetadataRevision PlaceholderTopic { get; }

        internal TopicMetadataRevision ManualTopic { get; }

        internal CompositionVersion SourceVersion { get; }

        internal CompositionVersion TargetVersion { get; }

        internal EntityId<EditorialRecordIdTag> ChapterEditorialRecordId { get; }

        internal EntityId<EditorialRecordIdTag> PublishingEditorialRecordId { get; }

        internal IReadOnlyList<AncillaryChapter> Chapters { get; }

        internal AncillaryPublishingText PublishingText { get; }

        internal IReadOnlyList<EntityId<EditorialRecordIdTag>> EditorialRecordIds
        { get; }

        internal StoredVersionEvidence ReviewEvidence { get; }

        /// <summary>
        /// The whole-source envelope the source-only pipeline composes from: it
        /// carries the revision id and every token inside its range, but it is
        /// not an utterance span.
        /// </summary>
        internal SourceSpan CreateFullSourceEnvelopeSpan() => new(
            EntityId<SourceSpanIdTag>.New(),
            Asset.Id,
            "audio:1",
            Range(0, 4),
            Asset.ContentDigest,
            Transcript.Id,
            Transcript.Utterances
                .SelectMany(static utterance => utterance.Tokens)
                .Select(static token => token.Id)
                .ToArray());

        internal StoredVersionEvidence CreateEmptyReviewEvidence(CompositionVersion version) =>
            new(version.Id, Project.Id, version.ContentDigest, [], [], []);

        /// <summary>
        /// A derived candidate span whose range covers only the first token of
        /// the first utterance, exactly as the emphasis/editorial detectors build it.
        /// </summary>
        internal SourceSpan CreateFirstTokenCandidateSpan()
        {
            Token token = Transcript.Utterances[0].Tokens[0];
            return new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                Asset.Id,
                "audio:1",
                token.Range!.Value,
                Asset.ContentDigest,
                Transcript.Id,
                [token.Id]);
        }

        internal EntityId<AncillaryArtifactExportIdTag> ExportId { get; }

        internal IReadOnlyDictionary<
            AncillaryArtifactKind,
            EntityId<ArtifactIdTag>> ArtifactIds
        { get; }

        internal ArtifactStore Store { get; }

        internal AncillaryArtifactExporter Exporter { get; }

        internal AncillaryArtifactExportRequest CreateRequest(
            EntityId<AncillaryArtifactExportIdTag>? exportId = null,
            CompositionVersion? compositionVersion = null,
            TranscriptRevision? transcriptRevision = null,
            IReadOnlyList<SourceSpan>? sourceSpans = null,
            IReadOnlyList<AncillaryChapter>? chapters = null,
            AncillaryPublishingText? publishingText = null,
            CompositionVersion? editingSourceVersion = null,
            bool omitEditingSourceVersion = false,
            StoredVersionEvidence? reviewEvidence = null,
            IReadOnlyList<EntityId<EditorialRecordIdTag>>? editorialRecordIds = null,
            IReadOnlyDictionary<
                AncillaryArtifactKind,
                EntityId<ArtifactIdTag>>? artifactIds = null,
            string language = "ja-JP",
            DateTimeOffset? createdAt = null,
            IReadOnlyList<AncillaryArtifactKind>? kinds = null) =>
            new(
                exportId ?? ExportId,
                Project,
                compositionVersion ?? TargetVersion,
                transcriptRevision ?? Transcript,
                sourceSpans ?? SourceSpans,
                chapters ?? Chapters,
                publishingText ?? PublishingText,
                omitEditingSourceVersion
                    ? null
                    : editingSourceVersion ?? SourceVersion,
                reviewEvidence ?? ReviewEvidence,
                editorialRecordIds ?? EditorialRecordIds,
                artifactIds ?? ArtifactIds,
                language,
                createdAt ?? CreatedAt,
                kinds);

        internal AncillaryArtifactExportRequest CreateReverseOrderedRequest()
        {
            var review = new StoredVersionEvidence(
                ReviewEvidence.CompositionVersionId,
                ReviewEvidence.ProjectId,
                ReviewEvidence.CompositionVersionDigest,
                ReviewEvidence.ReviewRecords.Reverse().ToArray(),
                ReviewEvidence.ReviewComments.Reverse().ToArray(),
                ReviewEvidence.Approvals.Reverse().ToArray());
            var publishing = new AncillaryPublishingText(
                PublishingText.Title,
                PublishingText.Summary,
                PublishingText.PostText,
                PublishingText.Hashtags.Reverse().ToArray());
            Dictionary<AncillaryArtifactKind, EntityId<ArtifactIdTag>> ids =
                ArtifactIds.Reverse().ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value);
            return CreateRequest(
                sourceSpans: SourceSpans.Reverse().ToArray(),
                publishingText: publishing,
                reviewEvidence: review,
                editorialRecordIds: EditorialRecordIds.Reverse().ToArray(),
                artifactIds: ids);
        }

        internal static IReadOnlyDictionary<
            AncillaryArtifactKind,
            EntityId<ArtifactIdTag>> CreateArtifactIds() =>
            Enum.GetValues<AncillaryArtifactKind>().ToDictionary(
                static kind => kind,
                static _ => EntityId<ArtifactIdTag>.New());

        internal byte[] ReadArtifactBytes(StoredArtifact artifact) =>
            File.ReadAllBytes(GetArtifactPath(artifact));

        internal string ReadArtifactText(AncillaryArtifactOutput output) =>
            new UTF8Encoding(false, true).GetString(
                ReadArtifactBytes(output.StoredArtifact));

        internal JsonDocument ReadArtifactDocument(
            AncillaryArtifactOutput output) =>
            JsonDocument.Parse(ReadArtifactBytes(output.StoredArtifact));

        internal string GetArtifactPath(StoredArtifact artifact) =>
            Path.Combine(
                WorkspacePath,
                artifact.StorageReference.Replace(
                    '/',
                    Path.DirectorySeparatorChar));

        internal string GetStagingDirectory(
            EntityId<AncillaryArtifactExportIdTag> exportId) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                Project.Id.ToString(),
                "staging",
                "ancillary",
                exportId.ToString());

        internal async Task RegisterConflictingArtifactAsync(
            EntityId<ArtifactIdTag> artifactId)
        {
            byte[] bytes = Encoding.UTF8.GetBytes("{\"conflict\":true}");
            string staging = Path.Combine(
                WorkspacePath,
                "projects",
                Project.Id.ToString(),
                "staging",
                "conflict",
                string.Concat(artifactId.ToString(), ".partial"));
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            File.WriteAllBytes(staging, bytes);
            _ = await Store.PromoteAsync(new ArtifactPromotionRequest(
                artifactId,
                Project.Id,
                ArtifactKind.Delivery,
                staging,
                string.Concat("conflict/", artifactId.ToString(), ".json"),
                ContentDigest.Compute(bytes),
                bytes.LongLength,
                CreatedAt));
        }

        internal async Task RegisterPathConflictingArtifactAsync()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("{\"pathConflict\":true}");
            string staging = Path.Combine(
                WorkspacePath,
                "projects",
                Project.Id.ToString(),
                "staging",
                "path-conflict",
                "transcript.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            File.WriteAllBytes(staging, bytes);
            _ = await Store.PromoteAsync(new ArtifactPromotionRequest(
                EntityId<ArtifactIdTag>.New(),
                Project.Id,
                ArtifactKind.Delivery,
                staging,
                string.Concat(
                    "ancillary/",
                    TargetVersion.Id.ToString(),
                    "/",
                    ExportId.ToString(),
                    "/transcript.json"),
                ContentDigest.Compute(bytes),
                bytes.LongLength,
                CreatedAt));
        }

        internal async Task<DeliveryCreationResult> CreateDeliveryAsync(
            AncillaryArtifactExportResult ancillary)
        {
            byte[] videoBytes = [0x00, 0x01, 0x02, 0x03];
            EntityId<ArtifactIdTag> videoArtifactId =
                EntityId<ArtifactIdTag>.New();
            string videoStaging = Path.Combine(
                WorkspacePath,
                "projects",
                Project.Id.ToString(),
                "staging",
                "delivery-integration",
                "video.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(videoStaging)!);
            File.WriteAllBytes(videoStaging, videoBytes);
            StoredArtifact storedVideo = await Store.PromoteAsync(
                new ArtifactPromotionRequest(
                    videoArtifactId,
                    Project.Id,
                    ArtifactKind.Render,
                    videoStaging,
                    "final/integration.mp4",
                    ContentDigest.Compute(videoBytes),
                    videoBytes.LongLength,
                    CreatedAt.AddMinutes(7)));
            var video = new ManifestArtifact(
                "video",
                "video/mp4",
                "ja-JP",
                storedVideo.ByteSize,
                storedVideo.ContentDigest,
                storedVideo.StorageReference);

            CompositionElement element = TargetVersion.Elements.Single();
            var timeline = new TimelineMap(
                EntityId<TimelineMapIdTag>.New(),
                TargetVersion.Id,
                [
                    new TimelineMapEntry(
                        element.OutputRange,
                        element.Origin,
                        element.SourceRange),
                ]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            var template = new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "Ancillary integration template",
                Digest('b'));
            var layout = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [
                    new LayoutRegion(
                        "main",
                        new NormalizedRect(
                            default,
                            default,
                            new RationalNumber(1, 1),
                            new RationalNumber(1, 1)),
                        LayoutRegionRole.Video),
                ],
                safeZones: []);
            ContentDigest layoutDigest = Digest('c');
            var profile = new RenderProfile(
                EntityId<RenderProfileIdTag>.New(),
                "ancillary-integration",
                Digest('d'),
                1920,
                1080,
                30,
                1,
                layout.Id,
                template.Id,
                template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: false);
            EntityId<RenderInputIdTag> inputId =
                EntityId<RenderInputIdTag>.New();
            var input = new RenderGraphInput(
                inputId,
                RenderInputKind.Video,
                Asset.ContentDigest,
                Asset.ByteSize,
                element.Origin);
            RenderNode videoNode = RenderNode.Video(
                EntityId<RenderNodeIdTag>.New(),
                inputId,
                element.OutputRange,
                element.Origin,
                element.SourceRange!.Value,
                "main");
            RenderNode outputNode = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [videoNode.Id],
                element.OutputRange);
            var graph = new RenderGraph(
                EntityId<RenderGraphIdTag>.New(),
                TargetVersion.Id,
                TargetVersion.ContentDigest,
                timeline.Id,
                timelineDigest,
                profile.Id,
                profile.ProfileDigest,
                layout.Id,
                layoutDigest,
                template,
                [input],
                fonts: [],
                [videoNode, outputNode],
                outputNode.Id);
            var context = new RenderGraphValidationContext(
                Project,
                TargetVersion,
                timeline,
                timelineDigest,
                profile,
                layout,
                layoutDigest,
                template,
                SourceSpans,
                EditorialRecordIds,
                [
                    new RenderArtifactEvidence(
                        inputId,
                        Asset.ContentDigest,
                        Asset.ByteSize),
                ],
                fonts: []);
            var renderManifest = new RenderManifest(
                EntityId<RenderManifestIdTag>.New(),
                TargetVersion,
                timeline,
                timelineDigest,
                profile,
                Digest('e'),
                "1.0.0",
                Digest('f'),
                [new RenderInputEvidence("source", Asset.ContentDigest)],
                [
                    new RenderToolEvidence(
                        "ffmpeg",
                        "8.0",
                        "release",
                        Digest('1'),
                        containerImageDigest: null),
                ],
                models: [],
                fonts: [],
                [video]);
            var qcRequest = new OutputQcRequest(
                videoArtifactId,
                renderManifest,
                graph,
                context,
                subtitleEvidence: null,
                TimeSpan.FromMinutes(1));
            var timing = new OutputQcStreamTiming(
                streamIndex: 0,
                [
                    new OutputQcTimingEntry(
                        sequence: 0,
                        presentationTimestampTicks: 0,
                        durationTicks: 2),
                ],
                new RationalTime(2, 1),
                presentationStart: default,
                presentationEnd: new RationalTime(2, 1),
                Digest('2'));
            var qualityControl = new OutputQcResult(
                storedVideo,
                qcRequest,
                Digest('3'),
                videoStreamIndex: 0,
                audioStreamIndex: null,
                subtitleStreamIndex: null,
                timing,
                audioTiming: null,
                subtitleTiming: null,
                new OutputQcDecodeEvidence(TimeSpan.Zero),
                new OutputQcLumaMeasurements(
                    [new OutputQcLumaFrameMeasurement(0, 0, 128m)],
                    Digest('4')),
                audioMeasurements: null);
            var service = new DeliveryService(
                static (_, approvals) => new ApprovalValidityResult(
                    approvals.ToArray(),
                    staleApprovals: []),
                Store.GetArtifact,
                Store.ValidateRegisteredAsync);
            KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>[] evidence =
            [
                new(videoArtifactId, video),
                .. ancillary.DeliveryArtifactEvidence,
            ];
            return await service.CreateAsync(
                EntityId<DeliveryManifestIdTag>.New(),
                Project,
                TargetVersion,
                renderManifest,
                context,
                qualityControl,
                ReviewEvidence.Approvals,
                evidence);
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

        private static TimeRange Range(long start, long duration) =>
            new(new RationalTime(start, 1), new RationalTime(duration, 1));
    }
}