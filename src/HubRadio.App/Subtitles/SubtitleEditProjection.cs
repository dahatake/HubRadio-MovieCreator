using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Subtitles;

internal enum SubtitleEditProjectionFailureReason
{
    InvalidCapture,
    InvalidTarget,
    MissingCue,
    AmbiguousCue,
    TimingConflict,
    LayoutConflict,
    InvalidActor,
    InvalidTimestamp,
    EvidenceMismatch,
    ResourceLimit,
}

/// <summary>Stable, content-private failure. Never silently omit a captured edit.</summary>
internal sealed class SubtitleEditProjectionException(SubtitleEditProjectionFailureReason reason)
    : Exception($"Subtitle edit projection stopped: {reason}.")
{
    internal SubtitleEditProjectionFailureReason Reason { get; } = reason;
}

/// <summary>
/// Complete parent spans from the caller's captured and new plans, never the
/// generated subtitle-candidate subsets. This is input evidence, NOT authority:
/// Apply binds every ID to the actual compositions and validates every candidate.
/// The coordinator must obtain these lists from its validated plan checkpoints.
/// </summary>
internal sealed class SubtitleProjectionCutCatalog
{
    internal const int MaximumTokenCount = 262_144;

    private SubtitleProjectionCutCatalog(IReadOnlyList<SourceSpan> sourceSpans, IReadOnlyList<SourceSpan> targetSpans)
    {
        SourceSpans = sourceSpans;
        TargetSpans = targetSpans;
        ContentDigest = SubtitleEditProjection.Hash(SubtitleEditProjection.Canonical(new
        {
            schema = "subtitle-edit.cut-catalog.v1",
            sourceSpans,
            targetSpans,
        }));
    }

    internal IReadOnlyList<SourceSpan> SourceSpans { get; }
    internal IReadOnlyList<SourceSpan> TargetSpans { get; }
    internal ContentDigest ContentDigest { get; }

    internal static SubtitleProjectionCutCatalog Create(IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<SourceSpan> targetSpans)
    {
        int tokens = 0;
        return new(Copy(sourceSpans, ref tokens), Copy(targetSpans, ref tokens));
    }

    private static IReadOnlyList<SourceSpan> Copy(IReadOnlyList<SourceSpan> spans, ref int tokens)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count > StandardRenderGraphFactory.MaximumCuts)
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.ResourceLimit);
        var copied = new SourceSpan[spans.Count];
        var ids = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = spans[index]
                ?? throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
            if (!ids.Add(span.Id))
                throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
            if (span.TranscriptTokenIds.Count > MaximumTokenCount - tokens)
                throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.ResourceLimit);
            tokens += span.TranscriptTokenIds.Count;
            copied[index] = span; // SourceSpan is sealed and owns an immutable token copy.
        }
        Array.Sort(copied, static (left, right) => StringComparer.Ordinal.Compare(left.Id.ToString(), right.Id.ToString()));
        return Array.AsReadOnly(copied);
    }

    internal SourceSpan GetSourceSpan(OriginRef origin) => Find(SourceSpans, origin);
    internal SourceSpan GetTargetSpan(OriginRef origin) => Find(TargetSpans, origin);

    private static SourceSpan Find(IReadOnlyList<SourceSpan> spans, OriginRef origin) =>
        origin.Kind == OriginKind.SourceSpan
            ? spans.SingleOrDefault(span => span.Id == origin.SourceSpanId)
                ?? throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch)
            : throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);

    internal void ValidateUse(IReadOnlyList<TimelineMapEntry> sourceCuts,
        IReadOnlyList<TimelineMapEntry> targetCuts, CancellationToken token)
    {
        ValidateSide(SourceSpans, sourceCuts, token);
        ValidateSide(TargetSpans, targetCuts, token);
    }

    private static void ValidateSide(IReadOnlyList<SourceSpan> spans, IReadOnlyList<TimelineMapEntry> cuts, CancellationToken token)
    {
        if (cuts.Count > StandardRenderGraphFactory.MaximumCuts)
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.ResourceLimit);
        var used = new HashSet<EntityId<SourceSpanIdTag>>();
        foreach (TimelineMapEntry cut in cuts)
        {
            token.ThrowIfCancellationRequested();
            if (cut.Origin.Kind != OriginKind.SourceSpan) { continue; }
            SourceSpan span = Find(spans, cut.Origin);
            if (cut.SourceRange is not TimeRange range || span.Range.Start > range.Start || span.Range.End < range.End)
                throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
            used.Add(span.Id);
        }
        if (used.Count != spans.Count)
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
    }

    internal static bool SameSource(SourceSpan left, SourceSpan right) =>
        left.AssetId == right.AssetId && left.SourceDigest == right.SourceDigest
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.TranscriptRevisionId == right.TranscriptRevisionId;

    internal static bool SameSpan(SourceSpan left, SourceSpan right) => SameSource(left, right)
        && left.Range == right.Range && left.TranscriptTokenIds.SequenceEqual(right.TranscriptTokenIds);

    internal static bool SameBinding(SourceSpan? left, SourceSpan? right) => left is null ? right is null
        : right is not null && left.Id == right.Id && SameSpan(left, right);
}

internal sealed record SubtitleEditProjectionResult
{
    internal SubtitleEditProjectionResult(SourceOnlyGeneratedSubtitleRuntimeEvidence output,
        SubtitleEditProjectionEvidence evidence)
    {
        evidence.ValidateOutput(output);
        Output = output;
        Evidence = evidence;
    }

    internal SourceOnlyGeneratedSubtitleRuntimeEvidence Output { get; }
    internal SubtitleEditProjectionEvidence Evidence { get; }
}

/// <summary>
/// Native derivation proof, NOT approval, a new human action, or a database receipt.
/// Its only factory requires a fully validated projection; JSON/hash/ID lists cannot
/// construct one. Persist the canonical evidence alongside the original capture and
/// regenerate it with Apply(savedCapture, freshGeneratedTarget, savedActor, savedAt,
/// cutCatalog: catalogRebuiltFromValidatedPlans) when parent span IDs differ.
/// The coordinator must pin that capture to the authoritative stored prefix and must
/// validate the target against its actual generation checkpoint. Hashes alone do not
/// authenticate either input. Never use a warnings flag as this proof.
/// </summary>
internal sealed class SubtitleEditProjectionEvidence
{
    private readonly SourceOnlyGeneratedSubtitleRuntimeEvidence output;
    private readonly SubtitleEditProjection.ValidatedProjection projection;
    private readonly IReadOnlyDictionary<EntityId<CueIdTag>, SubtitleEditProjection.ProjectedCue> projected;

    internal SubtitleEditProjectionEvidence(SubtitleEditProjection.ValidatedProjection projection,
        SourceOnlyGeneratedSubtitleRuntimeEvidence output)
    {
        ArgumentNullException.ThrowIfNull(projection);
        output = output with { Histories = Array.AsReadOnly(output.Histories.ToArray()) };
        projection.ValidateMaterialization(output);
        this.output = output;
        this.projection = projection;
        projected = projection.Cues.ToDictionary(cue => cue.Initial.CueId);
        ProjectionDepth = projection.ProjectionDepth;
        SourceHeadSnapshotDigest = projection.Source.HeadSnapshotDigest;
        SourceGeneratedInputDigest = projection.Source.GeneratedInputDigest;
        TargetGeneratedDigest = SubtitleEditProjection.GeneratedDigest(projection.Target);
        OutputDigest = SubtitleEditProjection.GeneratedDigest(output);
        ProjectedCueCount = projection.Cues.Count;
        TrustedEditorialRecordIds = Array.AsReadOnly(projection.Cues.SelectMany(cue => cue.Fields)
            .Select(field => field.Revision.Change.Origin.EditorialRecordId!)
            .Distinct().OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray());
        SubtitleEditCapture source = projection.Source;
        // Embed canonical JSON as JSON, not an escaped string whose backslashes
        // would double at every regeneration. The native capability remains separate.
        using JsonDocument? prior = source.InitialProjection is { } previous
            ? JsonDocument.Parse(previous.CanonicalJson, SubtitleEditProjection.EvidenceDocumentOptions) : null;
        CanonicalJson = SubtitleEditProjection.Canonical(new
        {
            schema = SubtitleEditProjection.RuleVersion,
            projectionDepth = ProjectionDepth,
            priorProofDigest = source.InitialProjection?.ContentDigest.Hex,
            priorProofCanonical = prior?.RootElement,
            source = new
            {
                source.ProjectId,
                source.RootVersionId,
                source.HeadVersionId,
                revision = source.Head.Revision,
                source.Sequence,
                generatedInputDigest = SourceGeneratedInputDigest.Hex,
                initialSnapshotDigest = source.InitialSnapshotDigest.Hex,
                headSnapshotDigest = SourceHeadSnapshotDigest.Hex,
                headCompositionDigest = source.Head.CompositionVersion.ContentDigest.Hex,
                recordPrefix = source.Edits.Select(row => new
                {
                    row.Sequence,
                    row.RequestId,
                    row.ParentVersionId,
                    row.ChildVersionId,
                    row.RootPayloadDigest,
                    row.ChildPayloadDigest,
                    row.ParentSnapshotDigest,
                    row.ResultSnapshotDigest,
                    row.CommitDigest,
                    row.PreviousRecordDigest,
                    row.RecordDigest,
                }).ToArray(),
            },
            target = new
            {
                versionId = output.Version.Id,
                versionDigest = output.Version.ContentDigest.Hex,
                versionPayloadDigest = SubtitleEditProjection.Hash(CompositionSnapshotJson.WriteVersion(output.Version)).Hex,
                generatedDigest = TargetGeneratedDigest.Hex,
                outputDigest = OutputDigest.Hex,
            },
            materialization = new
            {
                projection.ActorSid,
                projection.CreatedAt,
                reason = SubtitleEditProjection.MaterializationReason,
                projectedCueCount = ProjectedCueCount,
                newHumanActionCount = NewHumanActionCount,
            },
            cutCatalog = projection.CutCatalog is { } catalog ? new
            {
                sourceSpans = catalog.SourceSpans,
                targetSpans = catalog.TargetSpans,
                digest = catalog.ContentDigest.Hex,
            } : null,
            cues = projection.Cues.Select(cue => new
            {
                sourceCueId = cue.Source.CueId,
                sourceCandidateId = cue.Source.SourceCandidateId,
                sourceInitialRevisionId = cue.Source.Revisions[0].Id,
                sourceInitialSnapshotDigest = cue.Source.Revisions[0].ContentDigest.Hex,
                sourceRevisionId = cue.Source.CurrentRevision.Id,
                sourceRevisionDigest = cue.Source.CurrentRevision.ContentDigest.Hex,
                sourceIdentityDigest = SubtitleEditProjection.SourceIdentityDigest(cue.Source.SourceCandidate).Hex,
                targetCueId = cue.Initial.CueId,
                targetCandidateId = cue.Initial.SourceCandidateId,
                targetInitialRevisionId = cue.Initial.CurrentRevision.Id,
                targetInitialSnapshotDigest = cue.Initial.CurrentRevision.ContentDigest.Hex,
                materializedRevisionId = cue.RevisionId,
                snapshotDigest = SubtitleSnapshotJson.ComputeContentDigest(cue.Snapshot).Hex,
                sourceOrigin = cue.Initial.SourceCandidate.Origin,
                cue.SourceCut,
                cue.TargetCut,
                cue.SourceCutSpan,
                cue.TargetCutSpan,
                sourceCutIdentityDigest = SubtitleEditProjection.CutIdentityDigest(cue.SourceCut, cue.SourceCutSpan)?.Hex,
                targetCutIdentityDigest = SubtitleEditProjection.CutIdentityDigest(cue.TargetCut, cue.TargetCutSpan)?.Hex,
                outputOffset = SubtitleEditProjection.GeneratedRange(cue.Initial.SourceCandidate, cue.TargetCut).Start
                    - SubtitleEditProjection.GeneratedRange(cue.Source.SourceCandidate, cue.SourceCut).Start,
                fields = cue.Fields.Select(field => new
                {
                    kind = field.Revision.Change.Kind.ToString(),
                    origin = field.Revision.Change.Origin,
                    sourceProjectId = field.Record.ProjectId,
                    sourceRootVersionId = field.Record.RootVersionId,
                    sourceCueId = field.Revision.Snapshot.Cue.Id,
                    sourceGeneratedInputDigest = field.Record.GeneratedInputDigest.Hex,
                    sourceCut = field.SourceCut,
                    sourceCutSpan = field.SourceCutSpan,
                    sourceRevisionId = field.Revision.Id,
                    sourceRevisionDigest = field.Revision.ContentDigest.Hex,
                    sourceActorSid = field.Revision.ActorSid,
                    sourceReason = field.Revision.Reason,
                    sourceCreatedAt = field.Revision.CreatedAt,
                    field.Record.RequestId,
                    field.Record.Sequence,
                    field.Record.RecordDigest,
                    sourceFieldDigest = SubtitleEditProjection.FieldDigest(field.Revision.Change.Kind, field.Revision.Snapshot).Hex,
                    projectedFieldDigest = SubtitleEditProjection.FieldDigest(field.Revision.Change.Kind, cue.Snapshot).Hex,
                }).ToArray(),
            }).ToArray(),
            trustedEditorialRecordIds = TrustedEditorialRecordIds,
        });
        SubtitleEditProjection.ValidateEvidenceBudget(ProjectionDepth, Encoding.UTF8.GetByteCount(CanonicalJson));
        ContentDigest = SubtitleEditProjection.Hash(CanonicalJson);
    }

    internal string CanonicalJson { get; }
    internal ContentDigest ContentDigest { get; }
    internal ContentDigest SourceHeadSnapshotDigest { get; }
    internal ContentDigest SourceGeneratedInputDigest { get; }
    // Local complete target fingerprint, NOT SourceOnlyApplicationRegistrations' generated key.
    internal ContentDigest TargetGeneratedDigest { get; }
    internal ContentDigest OutputDigest { get; }
    internal int ProjectionDepth { get; }
    internal int ProjectedCueCount { get; }
    internal int NewHumanActionCount => 0;
    internal IReadOnlyList<EntityId<EditorialRecordIdTag>> TrustedEditorialRecordIds { get; }
    internal SubtitleProjectionCutCatalog? CutCatalog => projection.CutCatalog;

    internal SourceOnlyGeneratedSubtitleRuntimeEvidence GetValidatedOutput()
    {
        ValidateOutput(output);
        return output;
    }

    internal SourceSpan? GetTargetCutSpan(EntityId<CueIdTag> cueId) => projection.GetTargetCutSpan(cueId);

    internal void ValidateCutCorrespondence(CancellationToken token) => projection.ValidateCutCorrespondence(token);

    internal IReadOnlyList<SubtitleEditProjection.ProjectedField> GetInheritedFields(EntityId<CueIdTag> cueId)
    {
        ArgumentNullException.ThrowIfNull(cueId);
        if (!output.Histories.Any(history => history.CueId == cueId))
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        return Array.AsReadOnly(projected.TryGetValue(cueId, out var cue) ? cue.Fields.ToArray() : []);
    }

    // The prior target is the validated PURE generation baseline, even when the
    // actual materialized initial already has a manual output-range adjustment.
    internal TimeRange GetGeneratedInitialRange(EntityId<CueIdTag> cueId) =>
        projection.Target.Histories.Single(history => history.CueId == cueId).CurrentRevision.Snapshot.Cue.Range;

    /// <summary>
    /// Validate every output-owned part of the native initial against the actual
    /// materialization, not IDs alone. Host sources/templates/ancestor dictionaries
    /// remain checkpoint context and are pinned by the complete initial digest.
    /// </summary>
    internal void ValidateSnapshot(LayoutSubtitleBridgeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCutCorrespondence(cancellationToken);
        LayoutSpec layout = snapshot.ManualSelection?.Resolution.Plan?.DerivedLayoutSpec ?? snapshot.OutputEnvelope;
        if (snapshot.Revision != 0 || snapshot.ProjectId != output.Version.ProjectId
            || CompositionSnapshotJson.WriteVersion(snapshot.CompositionVersion) != CompositionSnapshotJson.WriteVersion(output.Version)
            || snapshot.OutputPixelWidth != output.Profile.Width || snapshot.OutputPixelHeight != output.Profile.Height
            || SubtitleEditProjection.Canonical(layout) != SubtitleEditProjection.Canonical(output.Layout)
            || snapshot.Subtitles.Count != output.Histories.Count || snapshot.Terops.Count != 0
            || snapshot.Keyframes.Count != 0 || snapshot.VariantPair is not null
            || snapshot.SubtitleDisplays.Count != 1 || snapshot.SubtitleDisplays[0].SelectedCueIds.Count != 0
            || SubtitleEditProjection.Canonical(snapshot.SubtitleDisplays[0].Profile)
                != SubtitleEditProjection.Canonical(output.Build.Subtitles.DisplayProfile))
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        string expectedProfile = SubtitleEditProjection.Canonical(SubtitleEditProjection.TargetLayoutProfile(output));
        string expectedLayout = SubtitleEditProjection.Canonical(output.Layout);
        for (int index = 0; index < output.Histories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LayoutSubtitleCueBinding binding = snapshot.Subtitles[index];
            if (SubtitleEditProjection.HistoryCanonical(binding.History) != SubtitleEditProjection.HistoryCanonical(output.Histories[index])
                || SubtitleEditProjection.Canonical(binding.LayoutSpec) != expectedLayout
                || SubtitleEditProjection.Canonical(binding.LayoutProfile) != expectedProfile
                || binding.ProhibitedRegions.Count != 0 || binding.FaceRegions.Count != 0)
                throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>The coordinator can additionally prove its complete checkpoint-specific layout builder.</summary>
    internal void ValidateSnapshot(LayoutSubtitleBridgeSnapshot snapshot,
        Func<SourceOnlyGeneratedSubtitleRuntimeEvidence, LayoutSubtitleBridgeSnapshot> buildSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildSnapshot);
        ValidateSnapshot(snapshot, cancellationToken);
        LayoutSubtitleBridgeSnapshot expected = buildSnapshot(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (DurableSubtitleEditService.ComputeSnapshotDigest(expected) != DurableSubtitleEditService.ComputeSnapshotDigest(snapshot))
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal void ValidateOutput(SourceOnlyGeneratedSubtitleRuntimeEvidence candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SubtitleEditProjection.GeneratedDigest(candidate) != OutputDigest)
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal void ValidateInitial(CompositionVersion version, SubtitleCueHistory history, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SubtitleCueHistory? expected = output.Histories.SingleOrDefault(value => value.CueId == history.CueId);
        if (expected is null || CompositionSnapshotJson.WriteVersion(version) != CompositionSnapshotJson.WriteVersion(output.Version)
            || SubtitleEditProjection.HistoryCanonical(history) != SubtitleEditProjection.HistoryCanonical(expected))
            throw new SubtitleEditProjectionException(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        token.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Pure, bounded reapplication of a source-only generated journal to a NEW generated
/// version. Does not change the ASR, source history, database, runtime, store or plan.
/// A capture rooted in a previously projected manual initial needs its original
/// native derivation proof; inherited fields keep their original journal evidence
/// instead of being legitimized through a fabricated Human transcript or new edit.
/// </summary>
internal static class SubtitleEditProjection
{
    internal const string RuleVersion = "subtitle-edit.projection.v1";
    internal const string MaterializationReason = "Materialize projected prior subtitle edits; not a new human action.";
    internal const int MaximumProjectionDepth = 128;
    internal const int MaximumCanonicalUtf8Bytes = 32 * 1024 * 1024;
    internal static JsonDocumentOptions EvidenceDocumentOptions => new() { MaxDepth = MaximumProjectionDepth + 64 };
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { MaxDepth = MaximumProjectionDepth + 64 };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static SubtitleEditProjectionResult Apply(SubtitleEditCapture source,
        SourceOnlyGeneratedSubtitleRuntimeEvidence target, string actorSid, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default, SubtitleProjectionCutCatalog? cutCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatedProjection proof = ValidatedProjection.Create(source, target, actorSid, createdAt, cancellationToken, cutCatalog);
        var projected = proof.Cues.ToDictionary(cue => cue.Initial.CueId);
        SubtitleCueHistory[] histories = proof.Target.Histories.Select(history => projected.ContainsKey(history.CueId)
            ? SubtitleService.MaterializeProjectedInitial(proof, history.CueId, cancellationToken) : history).ToArray();
        StandardSubtitleBuildResult original = proof.Target.Build;
        // Preserve both warning collections and the EXACT display/ASS profiles. These
        // fields have no place to store trust: the caller must carry Evidence separately.
        var input = new StandardSubtitleInput(histories, original.Subtitles.DisplayProfile,
            original.Subtitles.AssProfile, original.Subtitles.Warnings);
        var build = new StandardSubtitleBuildResult(input, original.Warnings, original.SegmentationRuleVersion, original.SettingsDigest);
        SourceOnlyGeneratedSubtitleRuntimeEvidence output = proof.Target with { Build = build, Histories = Array.AsReadOnly(histories) };
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = new SubtitleEditProjectionEvidence(proof, output);
        cancellationToken.ThrowIfCancellationRequested();
        return new SubtitleEditProjectionResult(output, evidence);
    }

    /// <summary>No caller-supplied fields/IDs/digests can mint this materialization capability.</summary>
    internal sealed class ValidatedProjection
    {
        private ValidatedProjection(SubtitleEditCapture source, SourceOnlyGeneratedSubtitleRuntimeEvidence target,
            string actorSid, DateTimeOffset createdAt, IReadOnlyList<ProjectedCue> cues, SubtitleProjectionCutCatalog? cutCatalog)
        {
            Source = source; Target = target; ActorSid = actorSid; CreatedAt = createdAt;
            Cues = Array.AsReadOnly(cues.ToArray());
            CutCatalog = cutCatalog;
            ProjectionDepth = (source.InitialProjection?.ProjectionDepth ?? 0) + 1;
        }

        internal SubtitleEditCapture Source { get; }
        internal SourceOnlyGeneratedSubtitleRuntimeEvidence Target { get; }
        internal string ActorSid { get; }
        internal DateTimeOffset CreatedAt { get; }
        internal IReadOnlyList<ProjectedCue> Cues { get; }
        internal int ProjectionDepth { get; }
        internal SubtitleProjectionCutCatalog? CutCatalog { get; }

        internal SourceSpan? GetTargetCutSpan(EntityId<CueIdTag> cueId)
        {
            ProjectedCue? cue = Cues.SingleOrDefault(value => value.Initial.CueId == cueId);
            if (cue is not null) { return cue.TargetCutSpan; }
            if (CutCatalog is null) { return null; }
            // An unedited cue still has validated full-parent evidence in the
            // catalog. Do not substitute its generated candidate's smaller span.
            SubtitleCueHistory history = Target.Histories.SingleOrDefault(value => value.CueId == cueId)
                ?? throw Failure(SubtitleEditProjectionFailureReason.EvidenceMismatch);
            TimelineMapEntry cut = MappedCut(history.SourceCandidate, history.CurrentRevision.Snapshot.Cue.Range, Target.Timeline.Entries);
            return CutCatalog.GetTargetSpan(cut.Origin);
        }

        internal static ValidatedProjection Create(SubtitleEditCapture source, SourceOnlyGeneratedSubtitleRuntimeEvidence target,
            string actorSid, DateTimeOffset createdAt, CancellationToken token, SubtitleProjectionCutCatalog? cutCatalog)
        {
            ValidateEvidenceBudget((source.InitialProjection?.ProjectionDepth ?? 0) + 1, 0);
            _ = Guard(() => TranscriptEditorContract.ValidateCanonicalSid(actorSid, nameof(actorSid)), SubtitleEditProjectionFailureReason.InvalidActor);
            source = Guard(() => ValidateCapture(source, token), SubtitleEditProjectionFailureReason.InvalidCapture);
            target = Guard(() => ValidateTarget(target, token), SubtitleEditProjectionFailureReason.InvalidTarget);
            if (target.Version.ProjectId != source.ProjectId || source.Head.CompositionVersions.ContainsKey(target.Version.Id))
                throw Failure(SubtitleEditProjectionFailureReason.InvalidTarget);
            if (createdAt.Offset != TimeSpan.Zero || createdAt < target.Version.CreatedAt
                || createdAt < source.Head.CompositionVersion.CreatedAt
                || target.Histories.Any(history => createdAt < history.CurrentRevision.CreatedAt)
                || source.Head.Subtitles.Any(binding => createdAt < binding.History.CurrentRevision.CreatedAt))
                throw Failure(SubtitleEditProjectionFailureReason.InvalidTimestamp);

            IReadOnlyList<TimelineMapEntry> sourceCuts = Guard(() => TimelineMapBuilder.Build(target.Timeline.Id,
                source.Head.GetComposition(source.RootVersionId)).Entries, SubtitleEditProjectionFailureReason.InvalidCapture);
            if (sourceCuts.Count > StandardRenderGraphFactory.MaximumCuts) { throw Failure(SubtitleEditProjectionFailureReason.ResourceLimit); }
            ValidateCatalog(source, target, sourceCuts, cutCatalog, token);
            ContentDigest targetDigest = GeneratedDigest(target);
            var cues = new List<ProjectedCue>();
            var used = new HashSet<EntityId<CueIdTag>>();
            foreach (LayoutSubtitleCueBinding binding in source.Head.Subtitles)
            {
                token.ThrowIfCancellationRequested();
                // Proves effective-head membership for an unchanged sibling, not just
                // a cue revision whose composition happens to have the same digest.
                _ = Guard(() => source.Head.WithInheritedComposition(binding.History), SubtitleEditProjectionFailureReason.InvalidCapture);
                SubtitleCueHistory history = binding.History;
                IReadOnlyList<ProjectedField> inherited = source.InitialProjection?.GetInheritedFields(history.CueId) ?? [];
                if (history.Revisions.Count == 1 && inherited.Count == 0) { continue; }
                SubtitleCueHistory initial = Match(source, history, target, sourceCuts, cutCatalog, out TimelineMapEntry sourceCut,
                    out TimelineMapEntry targetCut, out SourceSpan? sourceCutSpan, out SourceSpan? targetCutSpan);
                if (!used.Add(initial.CueId)) { throw Failure(SubtitleEditProjectionFailureReason.AmbiguousCue); }
                cues.Add(Guard(() => Project(source, binding, initial, target, targetDigest, sourceCut, targetCut, sourceCutSpan,
                    targetCutSpan, cutCatalog, inherited, actorSid, createdAt, token),
                    SubtitleEditProjectionFailureReason.InvalidCapture));
            }
            var replacements = cues.ToDictionary(cue => cue.Initial.CueId);
            ValidateRanges(target.Histories.Select(history => replacements.TryGetValue(history.CueId, out ProjectedCue? cue)
                ? cue.Snapshot : history.CurrentRevision.Snapshot));
            token.ThrowIfCancellationRequested();
            return new(source, target, actorSid, createdAt, cues, cutCatalog);
        }

        internal void ValidateCutCorrespondence(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Source.InitialProjection?.ValidateCutCorrespondence(token);
            IReadOnlyList<TimelineMapEntry> cuts = TimelineMapBuilder.Build(Target.Timeline.Id,
                Source.Head.GetComposition(Source.RootVersionId)).Entries;
            ValidateCatalog(Source, Target, cuts, CutCatalog, token);
            foreach (ProjectedCue cue in Cues)
            {
                token.ThrowIfCancellationRequested();
                SubtitleCueHistory selected = Match(Source, cue.Source, Target, cuts, CutCatalog,
                    out TimelineMapEntry sourceCut, out TimelineMapEntry targetCut,
                    out SourceSpan? sourceSpan, out SourceSpan? targetSpan);
                if (selected.CueId != cue.Initial.CueId || !sourceCut.Equals(cue.SourceCut) || !targetCut.Equals(cue.TargetCut)
                    || !SubtitleProjectionCutCatalog.SameBinding(sourceSpan, cue.SourceCutSpan)
                    || !SubtitleProjectionCutCatalog.SameBinding(targetSpan, cue.TargetCutSpan))
                    throw Failure(SubtitleEditProjectionFailureReason.EvidenceMismatch);
                foreach (ProjectedField field in cue.Fields)
                {
                    if (field.SourceCutSpan is { } span)
                        ValidateCandidateSpan(field.SourceBinding.History.SourceCandidate, field.SourceCut, span);
                    if (field.Revision.Change.Kind == SubtitleCueChangeKind.Timing
                        && !TimingOriginsCorrespond(field, sourceCut, sourceSpan))
                        throw Failure(SubtitleEditProjectionFailureReason.EvidenceMismatch);
                }
            }
        }

        internal void ValidateMaterialization(SourceOnlyGeneratedSubtitleRuntimeEvidence output)
        {
            var revised = Cues.ToDictionary(cue => cue.Initial.CueId);
            SubtitleCueHistory[] expected = Target.Histories.Select(history => revised.ContainsKey(history.CueId)
                ? SubtitleService.MaterializeProjectedInitial(this, history.CueId) : history).ToArray();
            var input = new StandardSubtitleInput(expected, Target.Build.Subtitles.DisplayProfile,
                Target.Build.Subtitles.AssProfile, Target.Build.Subtitles.Warnings);
            var build = new StandardSubtitleBuildResult(input, Target.Build.Warnings, Target.Build.SegmentationRuleVersion, Target.Build.SettingsDigest);
            if (GeneratedDigest(output) != GeneratedDigest(Target with { Build = build, Histories = Array.AsReadOnly(expected) }))
                throw Failure(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        }
    }

    internal sealed record ProjectedField(SubtitleCueRevision Revision, StoredSubtitleEdit Record,
        LayoutSubtitleCueBinding SourceBinding, TimelineMapEntry SourceCut, SourceSpan? SourceCutSpan = null);
    internal sealed record ProjectedCue(SubtitleCueHistory Source, SubtitleCueHistory Initial,
        SubtitleCueSnapshot Snapshot, EntityId<SubtitleCueRevisionIdTag> RevisionId,
        TimelineMapEntry SourceCut, TimelineMapEntry TargetCut, IReadOnlyList<ProjectedField> Fields,
        SourceSpan? SourceCutSpan = null, SourceSpan? TargetCutSpan = null);

    private static SubtitleEditCapture ValidateCapture(SubtitleEditCapture capture, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(capture.Head);
        ArgumentNullException.ThrowIfNull(capture.Edits);
        if (capture.Edits.Count > SubtitleEditStore.MaximumBranchEdits
            || capture.Head.Subtitles.Count > StandardSubtitleInput.MaximumCueCount
            || capture.Head.CompositionVersions.Count > 2 * SubtitleCompositionInheritance.MaxVersions
            || capture.Head.Subtitles.Any(binding => binding.History.Revisions.Count > capture.Edits.Count + 1))
            throw Failure(SubtitleEditProjectionFailureReason.ResourceLimit);
        var rows = new List<StoredSubtitleEdit>();
        long bytes = 0;
        foreach (StoredSubtitleEdit row in capture.Edits)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(row.AllocatedIds);
            if (row.AllocatedIds.Count is < 1 or > SubtitleEditStore.MaximumAllocatedIds)
                throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
            RequireCanonical(row.CommitCanonicalJson, SubtitleEditStore.MaximumCommitUtf8Bytes);
            RequireCanonical(row.ChildCanonicalJson, SubtitleEditStore.MaximumChildUtf8Bytes);
            RequireCanonical(row.OperationCanonicalJson, SubtitleEditStore.MaximumOperationUtf8Bytes);
            bytes += Utf8.GetByteCount(row.CommitCanonicalJson);
            if (bytes > SubtitleEditStore.MaximumBranchPayloadBytes) { throw Failure(SubtitleEditProjectionFailureReason.ResourceLimit); }
            rows.Add(row with { AllocatedIds = Array.AsReadOnly(row.AllocatedIds.ToArray()) });
        }
        capture = capture with { Edits = rows.AsReadOnly() };
        LayoutSubtitleBridgeSnapshot head = capture.Head;
        if (head.ProjectId != capture.ProjectId || head.Revision != rows.Count
            || capture.GeneratedInputDigest is null || capture.InitialSnapshotDigest is null
            || head.Terops.Count != 0 || head.Keyframes.Count != 0 || head.VariantPair is not null
            || DurableSubtitleEditService.ComputeSnapshotDigest(head) != capture.HeadSnapshotDigest)
            throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
        CompositionVersion root = head.GetComposition(capture.RootVersionId);
        foreach (var pair in head.CompositionVersions)
        {
            token.ThrowIfCancellationRequested();
            if (pair.Key != pair.Value.Id || pair.Value.ProjectId != capture.ProjectId) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
            ValidateComposition(pair.Value);
        }
        var children = rows.Select(row => row.ChildVersionId).ToHashSet();
        if (children.Contains(root.Id) || children.Count != rows.Count) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
        LayoutSubtitleCueBinding[] originals = head.Subtitles.Select(binding =>
        {
            token.ThrowIfCancellationRequested();
            SubtitleCueHistory history = binding.History;
            if (history.Revisions.Count > rows.Count + 1) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
            SubtitleCueRevision first = history.Revisions[0];
            var initial = new SubtitleCueHistory(history.SourceCandidate, [first]);
            if (capture.InitialProjection is { } prior)
            {
                SubtitleService.ValidateMaterializedInitial(root, initial, prior, token);
            }
            else
            {
                // This reserved reason is only a reason to REQUIRE proof, never
                // proof itself (e.g. timing-only or a style reverted to Normal).
                if (first.Reason == MaterializationReason) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
                SubtitleCueHistory generated = SubtitleService.CreateInitial(root, history.SourceCandidate, first.Snapshot.LayoutResolution,
                    history.CueId, first.Id, first.Snapshot.Cue.Range, first.ActorSid, first.CreatedAt, first.Reason, token);
                if (HistoryCanonical(initial) != HistoryCanonical(generated)) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
            }
            if (first.CreatedAt.Offset != TimeSpan.Zero) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
            return binding.WithHistory(initial);
        }).ToArray();
        var versions = head.CompositionVersions.Where(pair => !children.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var parent = new LayoutSubtitleBridgeSnapshot(head.ProjectId, 0, root, head.OutputEnvelope,
            head.OutputPixelWidth, head.OutputPixelHeight, head.Sources, head.Templates, originals, [], head.SubtitleDisplays,
            head.ManualSelection, compositionVersions: versions, subtitleOnly: head.SubtitleOnly);
        capture.InitialProjection?.ValidateSnapshot(parent, token);
        ContentDigest parentDigest = DurableSubtitleEditService.ComputeSnapshotDigest(parent);
        if (parentDigest != capture.InitialSnapshotDigest) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
        ContentDigest rootPayloadDigest = Hash(CompositionSnapshotJson.WriteVersion(root));
        ContentDigest? previous = null;
        var requests = new HashSet<Guid>();
        var allocations = new HashSet<Guid>(versions.Keys.Select(id => id.Value));
        for (int index = 0; index < rows.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            StoredSubtitleEdit row = rows[index];
            if (row.ProjectId != capture.ProjectId || row.RootVersionId != root.Id || row.Sequence != index + 1
                || row.ParentVersionId != parent.CompositionVersion.Id || row.GeneratedInputDigest != capture.GeneratedInputDigest
                || row.ParentSnapshotDigest != parentDigest || row.RootPayloadDigest != rootPayloadDigest
                || row.PreviousRecordDigest != previous || row.RequestId == Guid.Empty || !requests.Add(row.RequestId)
                || row.OccurredAt.Offset != TimeSpan.Zero || row.OccurredAt < parent.CompositionVersion.CreatedAt
                || row.AllocatedIds[0] != row.ChildVersionId.Value
                || row.AllocatedIds.Any(id => id == Guid.Empty || !allocations.Add(id))
                || row.CommitDigest != Hash(row.CommitCanonicalJson) || row.ChildPayloadDigest != Hash(row.ChildCanonicalJson)
                || row.RecordDigest != Digest(new
                {
                    schema = "subtitle-edit.record.v1",
                    sequence = row.Sequence,
                    commitDigest = row.CommitDigest.Hex,
                    rootPayloadDigest = row.RootPayloadDigest.Hex,
                    childPayloadDigest = row.ChildPayloadDigest.Hex,
                    previousRecordDigest = previous?.Hex,
                }))
                throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
            using JsonDocument operation = JsonDocument.Parse(row.OperationCanonicalJson);
            using JsonDocument childPayload = JsonDocument.Parse(row.ChildCanonicalJson);
            string expectedCommit = Canonical(new
            {
                schema = "subtitle-edit.commit.v1",
                requestId = row.RequestId.ToString("D"),
                projectId = row.ProjectId.ToString(),
                rootVersionId = row.RootVersionId.ToString(),
                parentVersionId = row.ParentVersionId.ToString(),
                childVersion = childPayload.RootElement,
                expectedSequence = index,
                generatedInputDigest = row.GeneratedInputDigest.Hex,
                parentSnapshotDigest = row.ParentSnapshotDigest.Hex,
                resultSnapshotDigest = row.ResultSnapshotDigest.Hex,
                operation = operation.RootElement,
                allocatedIds = row.AllocatedIds.Select(id => id.ToString("D")).ToArray(),
                actorSid = row.ActorSid,
                occurredAt = row.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            });
            var request = BridgeRequest.Create(row.RequestId, BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
            {
                projectId = capture.ProjectId.ToString(),
                expectedRevision = index,
                expectedVersionId = parent.CompositionVersion.Id.ToString(),
                expectedVersionDigest = parent.CompositionVersion.ContentDigest.Hex,
                operation = operation.RootElement,
            }));
            if (expectedCommit != row.CommitCanonicalJson || !LayoutSubtitleBridgeProtocol.HasValidParameters(request.Method, request.Parameters)
                || operation.RootElement.GetProperty("kind").GetString() != "editSubtitle")
                throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
            var ids = new Queue<Guid>(row.AllocatedIds);
            LayoutSubtitleBridgeOperationResult replay = LayoutSubtitleBridgeRuntime.Evaluate(parent, request, row.ActorSid,
                () => row.OccurredAt, ids.Dequeue, token);
            LayoutSubtitleBridgeSnapshot child = replay.Snapshot ?? throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
            if (replay.Status != LayoutSubtitleBridgeStatus.Available || ids.Count != 0 || child.Revision != index + 1
                || child.CompositionVersion.Id != row.ChildVersionId || child.CompositionVersion.ContentDigest != row.ChildContentDigest
                || child.CompositionVersion.CreatedAt != row.OccurredAt || row.ParentSnapshotDigest == row.ResultSnapshotDigest
                || CompositionSnapshotJson.WriteVersion(child.CompositionVersion) != row.ChildCanonicalJson
                || CompositionSnapshotJson.WriteVersion(head.GetComposition(row.ChildVersionId)) != row.ChildCanonicalJson
                || DurableSubtitleEditService.ComputeSnapshotDigest(child) != row.ResultSnapshotDigest)
                throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
            parent = child; parentDigest = row.ResultSnapshotDigest; previous = row.RecordDigest;
        }
        if (parent.CompositionVersion.Id != head.CompositionVersion.Id || parentDigest != capture.HeadSnapshotDigest)
            throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
        token.ThrowIfCancellationRequested();
        return capture;
    }

    private static SourceOnlyGeneratedSubtitleRuntimeEvidence ValidateTarget(SourceOnlyGeneratedSubtitleRuntimeEvidence target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target.Histories);
        if (target.Histories.Count > StandardSubtitleInput.MaximumCueCount || target.Timeline.Entries.Count > StandardRenderGraphFactory.MaximumCuts)
            throw Failure(SubtitleEditProjectionFailureReason.ResourceLimit);
        target = target with { Histories = Array.AsReadOnly(target.Histories.ToArray()) };
        ValidateComposition(target.Version, SubtitleEditProjectionFailureReason.InvalidTarget);
        StandardSubtitleInput input = target.Build.Subtitles;
        if (target.Timeline.CompositionVersionId != target.Version.Id
            || !TimelineMapBuilder.Build(target.Timeline.Id, target.Version).Entries.SequenceEqual(target.Timeline.Entries)
            || target.Profile.LayoutSpecId != target.Layout.Id
            || (BigInteger)target.Profile.Width * target.Layout.OutputAspectHeight != (BigInteger)target.Profile.Height * target.Layout.OutputAspectWidth
            || input.AssProfile.PlayResolutionWidth != target.Profile.Width || input.AssProfile.PlayResolutionHeight != target.Profile.Height
            || target.Histories.Count != input.Revisions.Count || target.Histories.Count != input.Candidates.Count
            || target.Histories.Select(history => history.CueId).Distinct().Count() != target.Histories.Count
            || target.Histories.Select(history => history.SourceCandidateId).Distinct().Count() != target.Histories.Count
            || target.Histories.Select(history => history.CurrentRevision.Id).Distinct().Count() != target.Histories.Count
            || target.Histories.Select(history => history.SourceCandidate.SourceSpan.Id).Distinct().Count() != target.Histories.Count)
            throw Failure(SubtitleEditProjectionFailureReason.InvalidTarget);
        var profile = TargetLayoutProfile(target);
        foreach (SubtitleCueHistory history in target.Histories)
        {
            token.ThrowIfCancellationRequested();
            SubtitleCueRevision revision = history.CurrentRevision;
            SubtitleCueCandidate candidate = history.SourceCandidate;
            SubtitleLayoutResolution layout = revision.Snapshot.LayoutResolution;
            if (history.Revisions.Count != 1 || history.Inheritance.Count != 0 || revision.CreatedAt.Offset != TimeSpan.Zero
                || revision.Reason == MaterializationReason
                || candidate.ManualTimingAdjustmentRequired || candidate.EvidenceTokenIds.Count == 0
                || !candidate.EvidenceTokenIds.SequenceEqual(candidate.SourceSpan.TranscriptTokenIds)
                || layout.LayoutSpecId != target.Layout.Id || layout.ProfileVersionDigest != profile.VersionDigest
                || !input.Candidates.Any(value => SubtitleServiceContract.CandidatesEqual(value, candidate))
                || !input.Revisions.Any(value => Canonical(value) == Canonical(revision)))
                throw Failure(SubtitleEditProjectionFailureReason.InvalidTarget);
            SubtitleCueHistory initial = SubtitleService.CreateInitial(target.Version, candidate, layout, history.CueId, revision.Id,
                revision.Snapshot.Cue.Range, revision.ActorSid, revision.CreatedAt, revision.Reason, token);
            if (HistoryCanonical(initial) != HistoryCanonical(history)) { throw Failure(SubtitleEditProjectionFailureReason.InvalidTarget); }
            _ = MappedCut(candidate, revision.Snapshot.Cue.Range, target.Timeline.Entries);
            SubtitlePlacementCandidate placement = layout.Plan?.Placement ?? throw Failure(SubtitleEditProjectionFailureReason.LayoutConflict);
            SubtitleLayoutResolution resolved = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(candidate, target.Layout,
                profile, [placement], [], [], null), token);
            if (resolved.Status != SubtitleLayoutResolutionStatus.Resolved) { throw Failure(SubtitleEditProjectionFailureReason.LayoutConflict); }
            _ = Guard(() => { StandardSubtitlePlacement.Validate(input.AssProfile, layout); return true; }, SubtitleEditProjectionFailureReason.LayoutConflict);
        }
        return target;
    }

    private static SubtitleCueHistory Match(SubtitleEditCapture capture, SubtitleCueHistory source,
        SourceOnlyGeneratedSubtitleRuntimeEvidence target, IReadOnlyList<TimelineMapEntry> sourceCuts,
        SubtitleProjectionCutCatalog? catalog, out TimelineMapEntry sourceCut, out TimelineMapEntry targetCut,
        out SourceSpan? sourceCutSpan, out SourceSpan? targetCutSpan)
    {
        TimeRange generatedRange = capture.InitialProjection?.GetGeneratedInitialRange(source.CueId)
            ?? source.Revisions[0].Snapshot.Cue.Range;
        sourceCut = MappedCut(source.SourceCandidate, generatedRange, sourceCuts);
        sourceCutSpan = ResolveSourceCutSpan(capture, source, sourceCut, catalog);
        ContentDigest identity = SourceIdentityDigest(source.SourceCandidate);
        SubtitleCueHistory[] occurrences = capture.Head.Subtitles.Select(binding => binding.History)
            .Where(history => SourceIdentityDigest(history.SourceCandidate) == identity).ToArray();
        SubtitleCueHistory[] matches = target.Histories.Where(history => SourceIdentityDigest(history.SourceCandidate) == identity).ToArray();
        if (matches.Length == 0) { throw Failure(SubtitleEditProjectionFailureReason.MissingCue); }
        OriginRef cutOrigin = sourceCut.Origin;
        int sourceOccurrences = sourceCuts.Count(entry => entry.Origin.Kind == OriginKind.SourceSpan
            && (catalog is null ? entry.Origin == cutOrigin
                : SubtitleProjectionCutCatalog.SameSpan(catalog.GetSourceSpan(entry.Origin), catalog.GetSourceSpan(cutOrigin)))
            && entry.SourceRange is TimeRange range && Contains(range, source.SourceCandidate.SourceRange));
        int targetOccurrences = target.Timeline.Entries.Count(entry => entry.Origin.Kind == OriginKind.SourceSpan
            && (catalog is null ? entry.Origin == cutOrigin
                : SubtitleProjectionCutCatalog.SameSpan(catalog.GetTargetSpan(entry.Origin), catalog.GetSourceSpan(cutOrigin)))
            && entry.SourceRange is TimeRange range && Contains(range, source.SourceCandidate.SourceRange));
        if (sourceOccurrences > 1 || targetOccurrences > 1 || occurrences.Length > 1 || matches.Length > 1)
        {
            // Repeated tokens are not an occurrence identity. Require the complete
            // original cut and output offset, with equal multiplicity; no ordinal guess.
            if (sourceOccurrences != targetOccurrences || occurrences.Length != matches.Length)
                throw Failure(SubtitleEditProjectionFailureReason.AmbiguousCue);
            TimelineMapEntry expected = sourceCut;
            matches = matches.Where(history =>
            {
                TimelineMapEntry cut = MappedCut(history.SourceCandidate, history.CurrentRevision.Snapshot.Cue.Range, target.Timeline.Entries);
                return cut.OutputRange == expected.OutputRange && cut.SourceRange == expected.SourceRange
                    && CutOriginsCorrespond(expected, cut, catalog);
            }).ToArray();
        }
        if (matches.Length != 1) { throw Failure(SubtitleEditProjectionFailureReason.AmbiguousCue); }
        SubtitleCueHistory selected = matches[0];
        targetCut = MappedCut(selected.SourceCandidate, selected.CurrentRevision.Snapshot.Cue.Range, target.Timeline.Entries);
        // Without a caller-supplied, fully validated catalog the legacy ID rule
        // remains strict. Equal positions or source-asset-only context prove nothing.
        if (!CutOriginsCorrespond(sourceCut, targetCut, catalog)
            || !capture.Head.Sources.Any(binding => binding.SourceAssetId == source.SourceCandidate.SourceSpan.AssetId))
            throw Failure(SubtitleEditProjectionFailureReason.MissingCue);
        targetCutSpan = catalog?.GetTargetSpan(targetCut.Origin) ?? sourceCutSpan;
        if (targetCutSpan is not null) { ValidateCandidateSpan(selected.SourceCandidate, targetCut, targetCutSpan); }
        return selected;
    }

    private static ProjectedCue Project(SubtitleEditCapture capture, LayoutSubtitleCueBinding binding,
        SubtitleCueHistory initial, SourceOnlyGeneratedSubtitleRuntimeEvidence target, ContentDigest targetDigest,
        TimelineMapEntry sourceCut, TimelineMapEntry targetCut, SourceSpan? sourceCutSpan, SourceSpan? targetCutSpan,
        SubtitleProjectionCutCatalog? catalog, IReadOnlyList<ProjectedField> inherited,
        string actorSid, DateTimeOffset createdAt, CancellationToken token)
    {
        SubtitleCueHistory history = binding.History;
        var final = inherited.ToDictionary(field => field.Revision.Change.Kind, field =>
        {
            // A strict-ID hop may predate the first catalog. Only that SAME actual
            // origin can attach its now-available full parent span to an old field.
            if (field.SourceCutSpan is null && sourceCutSpan is not null && field.SourceCut.Origin == sourceCut.Origin)
            {
                ValidateCandidateSpan(field.SourceBinding.History.SourceCandidate, field.SourceCut, sourceCutSpan);
                return field with { SourceCutSpan = sourceCutSpan };
            }
            return field;
        });
        foreach (SubtitleCueRevision revision in history.Revisions.Skip(1))
        {
            // Only these NEW revisions belong to this branch's journal. Inherited
            // records retain their original root, actor, origin, sequence and clock.
            final[revision.Change.Kind] = new ProjectedField(revision,
                capture.Edits.Single(row => row.ChildVersionId == revision.CompositionVersionId), binding, sourceCut, sourceCutSpan);
        }
        if (final.Keys.Any(kind => kind is not (SubtitleCueChangeKind.Text or SubtitleCueChangeKind.Timing or SubtitleCueChangeKind.Style or SubtitleCueChangeKind.Layout)))
            throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture);
        SubtitleCueSnapshot baseline = initial.CurrentRevision.Snapshot;
        SubtitleCueSnapshot source = history.CurrentRevision.Snapshot;
        TimeRange range = baseline.Cue.Range;
        if (final.TryGetValue(SubtitleCueChangeKind.Timing, out ProjectedField? timing))
        {
            range = Guard(() =>
            {
                // The human timing is absolute in the ORIGINAL record's output
                // clock. Never subtract an already-adjusted materialized initial.
                TimeRange original = timing.Revision.Snapshot.Cue.Range;
                TimeRange originalGenerated = GeneratedRange(timing.SourceBinding.History.SourceCandidate, timing.SourceCut);
                TimeRange currentGenerated = GeneratedRange(history.SourceCandidate, sourceCut);
                var current = new TimeRange(original.Start + currentGenerated.Start - originalGenerated.Start, original.Duration);
                var mapped = new TimeRange(original.Start + baseline.Cue.Range.Start - originalGenerated.Start, original.Duration);
                if (SourceIdentityDigest(timing.SourceBinding.History.SourceCandidate) != SourceIdentityDigest(history.SourceCandidate)
                    || !TimingOriginsCorrespond(timing, sourceCut, sourceCutSpan) || current != source.Cue.Range
                    || !Contains(timing.SourceCut.OutputRange, original)
                    || !Contains(sourceCut.OutputRange, current) || !Contains(targetCut.OutputRange, mapped)
                    || mapped.Start < default(RationalTime) || mapped.Duration <= default(RationalTime))
                    throw Failure(SubtitleEditProjectionFailureReason.TimingConflict);
                return mapped;
            }, SubtitleEditProjectionFailureReason.TimingConflict);
        }
        SubtitleLayoutResolution layout = baseline.LayoutResolution;
        if (final.TryGetValue(SubtitleCueChangeKind.Layout, out ProjectedField? placementField))
        {
            layout = Guard(() =>
            {
                // Only an actual explicit-placement edit can supply a manual rect.
                // Changed aspect ratios/custom constraints have no proven transform.
                SubtitleCueRevision placementRevision = placementField.Revision;
                LayoutSubtitleCueBinding context = placementField.SourceBinding;
                StoredSubtitleEdit row = placementField.Record;
                using JsonDocument operation = JsonDocument.Parse(row.OperationCanonicalJson);
                SubtitlePlacementCandidate placement = placementRevision.Snapshot.ResolvedPlacement ?? throw Failure(SubtitleEditProjectionFailureReason.LayoutConflict);
                if (operation.RootElement.GetProperty("edit").GetProperty("kind").GetString() != "placement"
                    || (BigInteger)context.LayoutSpec.OutputAspectWidth * target.Layout.OutputAspectHeight
                        != (BigInteger)target.Layout.OutputAspectWidth * context.LayoutSpec.OutputAspectHeight
                    || context.ProhibitedRegions.Count != 0 || context.FaceRegions.Count != 0
                    || !context.LayoutProfile.RequiredSafeZoneNames.SequenceEqual(context.LayoutSpec.SafeZones.Select(zone => zone.Name))
                    || context.LayoutProfile.SoftConstraintWeights.Any(weight => weight.Weight != 0))
                    throw Failure(SubtitleEditProjectionFailureReason.LayoutConflict);
                var explicitPlacement = new SubtitlePlacementCandidate(StableId<SubtitlePlacementCandidateIdTag>(new
                {
                    rule = RuleVersion,
                    initial.SourceCandidateId,
                    versionId = target.Version.Id,
                    sourceRevisionId = placementRevision.Id,
                    placement.RegionName,
                    placement.Rect,
                }), placement.RegionName, placement.Rect);
                SubtitleLayoutResolution resolved = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(initial.SourceCandidate,
                    target.Layout, TargetLayoutProfile(target), [explicitPlacement], [], [], baseline.ResolvedPlacement?.Rect), token);
                if (resolved.Status != SubtitleLayoutResolutionStatus.Resolved) { throw Failure(SubtitleEditProjectionFailureReason.LayoutConflict); }
                StandardSubtitlePlacement.Validate(target.Build.Subtitles.AssProfile, resolved);
                return resolved;
            }, SubtitleEditProjectionFailureReason.LayoutConflict);
        }
        var snapshot = new SubtitleCueSnapshot(new Cue(initial.CueId, CueKind.Subtitle, range, layout.Plan?.Placement.RegionName),
            final.TryGetValue(SubtitleCueChangeKind.Text, out ProjectedField? text) ? text.Revision.Snapshot.Text : baseline.Text,
            final.TryGetValue(SubtitleCueChangeKind.Style, out ProjectedField? style) ? style.Revision.Snapshot.Style : baseline.Style,
            text is not null ? text.Revision.Change.Origin : baseline.TextOrigin, layout);
        ProjectedField[] fields = final.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
        EntityId<SubtitleCueRevisionIdTag> id = StableId<SubtitleCueRevisionIdTag>(new
        {
            rule = RuleVersion,
            capture.RootVersionId,
            capture.HeadVersionId,
            capture.HeadSnapshotDigest,
            capture.GeneratedInputDigest,
            recordDigest = capture.Edits.LastOrDefault()?.RecordDigest,
            priorProofDigest = capture.InitialProjection?.ContentDigest.Hex,
            cutCatalogDigest = catalog?.ContentDigest.Hex,
            target.Version.Id,
            target = targetDigest.Hex,
            initial.CueId,
            snapshot = SubtitleSnapshotJson.ComputeContentDigest(snapshot).Hex,
            actorSid,
            createdAt,
        });
        if (capture.Head.Subtitles.SelectMany(value => value.History.Revisions).Any(revision => revision.Id == id)
            || target.Histories.Any(value => value.CurrentRevision.Id == id))
            throw Failure(SubtitleEditProjectionFailureReason.InvalidTarget);
        return new(history, initial, snapshot, id, sourceCut, targetCut, Array.AsReadOnly(fields), sourceCutSpan, targetCutSpan);
    }

    private static void ValidateCatalog(SubtitleEditCapture source, SourceOnlyGeneratedSubtitleRuntimeEvidence target,
        IReadOnlyList<TimelineMapEntry> sourceCuts, SubtitleProjectionCutCatalog? catalog, CancellationToken token)
    {
        if (catalog is null) { return; }
        catalog.ValidateUse(sourceCuts, target.Timeline.Entries, token);
        foreach (LayoutSubtitleCueBinding binding in source.Head.Subtitles)
        {
            token.ThrowIfCancellationRequested();
            SubtitleCueHistory history = binding.History;
            TimeRange range = source.InitialProjection?.GetGeneratedInitialRange(history.CueId) ?? history.Revisions[0].Snapshot.Cue.Range;
            _ = ResolveSourceCutSpan(source, history, MappedCut(history.SourceCandidate, range, sourceCuts), catalog);
        }
        foreach (SubtitleCueHistory history in target.Histories)
        {
            token.ThrowIfCancellationRequested();
            TimelineMapEntry cut = MappedCut(history.SourceCandidate, history.CurrentRevision.Snapshot.Cue.Range, target.Timeline.Entries);
            ValidateCandidateSpan(history.SourceCandidate, cut, catalog.GetTargetSpan(cut.Origin));
        }
    }

    private static SourceSpan? ResolveSourceCutSpan(SubtitleEditCapture capture, SubtitleCueHistory history,
        TimelineMapEntry cut, SubtitleProjectionCutCatalog? catalog)
    {
        SourceSpan? prior = capture.InitialProjection?.GetTargetCutSpan(history.CueId);
        SourceSpan? span = catalog?.GetSourceSpan(cut.Origin) ?? prior;
        if (span is not null) { ValidateCandidateSpan(history.SourceCandidate, cut, span); }
        if (prior is not null && !SubtitleProjectionCutCatalog.SameBinding(prior, span))
            throw Failure(SubtitleEditProjectionFailureReason.EvidenceMismatch);
        return span;
    }

    private static void ValidateCandidateSpan(SubtitleCueCandidate candidate, TimelineMapEntry cut, SourceSpan span)
    {
        if (cut.Origin.Kind != OriginKind.SourceSpan || cut.Origin.SourceSpanId != span.Id
            || cut.SourceRange is not TimeRange range || !Contains(span.Range, range) || !Contains(range, candidate.SourceRange)
            || !SubtitleProjectionCutCatalog.SameSource(span, candidate.SourceSpan)
            || span.TranscriptRevisionId != candidate.SourceRevisionId || candidate.EvidenceTokenIds.Count == 0
            || !candidate.EvidenceTokenIds.SequenceEqual(candidate.SourceSpan.TranscriptTokenIds)
            || !ContainsTokens(span.TranscriptTokenIds, candidate.EvidenceTokenIds))
            throw Failure(SubtitleEditProjectionFailureReason.EvidenceMismatch);
    }

    private static bool ContainsTokens(IReadOnlyList<EntityId<TranscriptTokenIdTag>> parent,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> selected)
    {
        // Parent IDs are unique, so the first selected ID has at most one offset.
        for (int start = 0; start <= parent.Count - selected.Count; start++)
        {
            if (parent[start] != selected[0]) { continue; }
            for (int offset = 1; offset < selected.Count; offset++)
                if (parent[start + offset] != selected[offset]) { return false; }
            return true;
        }
        return false;
    }

    private static bool CutOriginsCorrespond(TimelineMapEntry source, TimelineMapEntry target, SubtitleProjectionCutCatalog? catalog) =>
        catalog is null ? source.Origin == target.Origin
            : SubtitleProjectionCutCatalog.SameSpan(catalog.GetSourceSpan(source.Origin), catalog.GetTargetSpan(target.Origin))
                && (source.Origin == target.Origin || source.SourceRange == target.SourceRange);

    private static bool TimingOriginsCorrespond(ProjectedField field, TimelineMapEntry current, SourceSpan? currentSpan) =>
        field.SourceCutSpan is { } original && currentSpan is not null
            ? SubtitleProjectionCutCatalog.SameSpan(original, currentSpan)
            : field.SourceCut.Origin == current.Origin;

    internal static ContentDigest? CutIdentityDigest(TimelineMapEntry cut, SourceSpan? span) => span is null ? null : Digest(new
    {
        span.AssetId,
        span.SourceDigest,
        span.StreamId,
        span.TranscriptRevisionId,
        span.TranscriptTokenIds,
        span.Range,
        cut.SourceRange,
    });

    internal static SubtitleLayoutProfile TargetLayoutProfile(SourceOnlyGeneratedSubtitleRuntimeEvidence target) =>
        new(target.Layout.Id, target.Build.Subtitles.DisplayProfile.VersionDigest, target.Layout.SafeZones.Select(zone => zone.Name).ToArray(),
            Enum.GetValues<SubtitleSoftConstraintKind>().Select(kind => new SubtitleSoftConstraintWeight(kind, 0)).ToArray());

    private static TimelineMapEntry MappedCut(SubtitleCueCandidate candidate, TimeRange initialRange, IReadOnlyList<TimelineMapEntry> entries)
    {
        TimelineMapEntry[] matches = entries.Where(entry => entry.Origin.Kind == OriginKind.SourceSpan
            && entry.SourceRange is TimeRange range && Contains(range, candidate.SourceRange)
            && initialRange == new TimeRange(entry.OutputRange.Start + candidate.SourceRange.Start - range.Start, candidate.SourceRange.Duration)).ToArray();
        return matches.Length == 1 ? matches[0] : throw Failure(SubtitleEditProjectionFailureReason.AmbiguousCue);
    }

    private static void ValidateRanges(IEnumerable<SubtitleCueSnapshot> snapshots)
    {
        RationalTime? previousEnd = null;
        foreach (SubtitleCueSnapshot snapshot in snapshots.OrderBy(value => value.Cue.Range.Start))
        {
            TimeRange range = snapshot.Cue.Range;
            if (range.Start < default(RationalTime) || range.Duration <= default(RationalTime)
                || previousEnd is RationalTime end && range.Start < end)
                throw Failure(SubtitleEditProjectionFailureReason.TimingConflict);
            previousEnd = range.End;
        }
    }

    internal static ContentDigest SourceIdentityDigest(SubtitleCueCandidate candidate) => Digest(new
    {
        candidate.SourceSpan.AssetId,
        candidate.SourceSpan.SourceDigest,
        candidate.SourceSpan.StreamId,
        candidate.SourceRevisionId,
        candidate.SourceRevisionDigest,
        candidate.EvidenceTokenIds,
        candidate.SourceRange,
        // Exact source text equality is an additional check, never similarity or replacement ASR.
        candidate.Text,
        candidate.SpeakerId,
    });

    internal static ContentDigest FieldDigest(SubtitleCueChangeKind kind, SubtitleCueSnapshot snapshot) => Digest(new
    {
        schema = "subtitle-edit.projected-field.v1",
        kind = kind.ToString(),
        value = kind switch
        {
            SubtitleCueChangeKind.Text => (object)new { snapshot.Text, snapshot.TextOrigin },
            SubtitleCueChangeKind.Timing => snapshot.Cue.Range,
            SubtitleCueChangeKind.Style => snapshot.Style,
            SubtitleCueChangeKind.Layout => new { snapshot.LayoutResolution.LayoutSpecId, snapshot.LayoutResolution.ProfileVersionDigest, snapshot.ResolvedPlacement },
            _ => throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture),
        },
    });

    internal static string HistoryCanonical(SubtitleCueHistory history) => Canonical(new
    {
        history.SourceCandidate,
        history.ProjectId,
        history.CueId,
        history.Revisions,
        inheritance = history.Inheritance.Select(path => new
        {
            path.RevisionId,
            versions = path.Versions.Select(CompositionSnapshotJson.WriteVersion).ToArray(),
        }).ToArray(),
    });

    internal static ContentDigest GeneratedDigest(SourceOnlyGeneratedSubtitleRuntimeEvidence value) => Digest(new
    {
        schema = "subtitle-edit.projection-generated.v1",
        version = CompositionSnapshotJson.WriteVersion(value.Version),
        value.Timeline,
        value.Layout,
        value.Profile,
        value.Build,
        histories = value.Histories.Select(HistoryCanonical).ToArray(),
    });

    private static void ValidateComposition(CompositionVersion version,
        SubtitleEditProjectionFailureReason reason = SubtitleEditProjectionFailureReason.InvalidCapture)
    {
        if (version.CreatedAt.Offset != TimeSpan.Zero || version.ContentDigest != ContentDigest.Compute(
            CompositionSnapshotJson.WriteElementsObjectToUtf8(version.Elements)))
            throw Failure(reason);
    }

    private static void RequireCanonical(string json, int maximum)
    {
        int bytes = Utf8.GetByteCount(json);
        if (bytes is 0 || bytes > maximum) { throw Failure(SubtitleEditProjectionFailureReason.ResourceLimit); }
        if (HubRadio.Core.Serialization.CanonicalJson.Canonicalize(json) != json) { throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture); }
    }

    internal static TimeRange GeneratedRange(SubtitleCueCandidate candidate, TimelineMapEntry cut) =>
        new(cut.OutputRange.Start + candidate.SourceRange.Start
            - (cut.SourceRange ?? throw Failure(SubtitleEditProjectionFailureReason.InvalidCapture)).Start, candidate.SourceRange.Duration);

    internal static void ValidateEvidenceBudget(int projectionDepth, long canonicalUtf8Bytes)
    {
        if (projectionDepth is < 1 or > MaximumProjectionDepth || canonicalUtf8Bytes is < 0 or > MaximumCanonicalUtf8Bytes)
            throw Failure(SubtitleEditProjectionFailureReason.ResourceLimit);
    }

    private static bool Contains(TimeRange outer, TimeRange inner) => outer.Start <= inner.Start && inner.End <= outer.End;
    internal static string Canonical(object value)
    {
        // Bound serialization BEFORE materializing/canonicalizing a proof chain.
        // Core.Write retains the existing number/string/order rules, while parsing
        // explicitly permits the bounded projection depth rather than JSON's default 64.
        using var buffer = new BoundedJsonStream();
        JsonSerializer.Serialize(buffer, value, EvidenceJsonOptions);
        using JsonDocument document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), EvidenceDocumentOptions);
        byte[] canonical = HubRadio.Core.Serialization.CanonicalJson.Write(document.RootElement);
        ValidateEvidenceBudget(1, canonical.Length);
        return Utf8.GetString(canonical);
    }

    private sealed class BoundedJsonStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Demand(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Demand(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Demand(1);
            base.WriteByte(value);
        }

        private void Demand(int count)
        {
            if (count > MaximumCanonicalUtf8Bytes - Length) { throw Failure(SubtitleEditProjectionFailureReason.ResourceLimit); }
        }
    }

    internal static ContentDigest Hash(string value) => ContentDigest.Compute(Utf8.GetBytes(value));
    private static ContentDigest Digest(object value) => Hash(Canonical(value));
    private static EntityId<T> StableId<T>(object value) => EntityId<T>.From(new Guid(Convert.FromHexString(Digest(value).Hex)[..16]));
    private static SubtitleEditProjectionException Failure(SubtitleEditProjectionFailureReason reason) => new(reason);

    private static T Guard<T>(Func<T> action, SubtitleEditProjectionFailureReason reason)
    {
        try { return action(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException
            or FormatException or KeyNotFoundException or OverflowException or NullReferenceException
            or SubtitleServiceException or StandardGenerationException or SubtitleDisplayException or LayoutSubtitleBridgeConflictException)
        { throw Failure(reason); }
    }
}