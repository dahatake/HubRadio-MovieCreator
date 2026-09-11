using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.App.Rendering;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Subtitles;

/// <summary>Content-private, stable evidence. A source range is never an invented token time.</summary>
public sealed class StandardSubtitleBuildWarning
{
    internal StandardSubtitleBuildWarning(string key, string rule, string reason,
        bool isUnresolved, TimeRange? sourceRange = null, EntityId<SourceSpanIdTag>? sourceSpanId = null)
    {
        Key = key;
        Rule = rule;
        Reason = reason;
        IsUnresolved = isUnresolved;
        SourceRange = sourceRange;
        SourceSpanId = sourceSpanId;
    }

    public string Key { get; }
    public string Rule { get; }
    public string Reason { get; }
    public bool IsUnresolved { get; }
    public TimeRange? SourceRange { get; }
    public EntityId<SourceSpanIdTag>? SourceSpanId { get; }
}

public sealed class StandardSubtitleBuildResult
{
    internal StandardSubtitleBuildResult(StandardSubtitleInput subtitles,
        IReadOnlyList<StandardSubtitleBuildWarning> warnings, string segmentationRuleVersion,
        ContentDigest settingsDigest)
    {
        Subtitles = subtitles;
        Warnings = Array.AsReadOnly(warnings.ToArray());
        SegmentationRuleVersion = segmentationRuleVersion;
        SettingsDigest = settingsDigest;
    }

    public StandardSubtitleInput Subtitles { get; }
    public IReadOnlyList<StandardSubtitleBuildWarning> Warnings { get; }
    public string SegmentationRuleVersion { get; }
    public ContentDigest SettingsDigest { get; }
    public bool HasUnresolvedCues => Warnings.Any(static warning => warning.IsUnresolved);
}

/// <summary>The strict Build entry point does not silently discard unresolved subtitle evidence.</summary>
public sealed class StandardSubtitleBuildException : Exception
{
    internal StandardSubtitleBuildException(StandardSubtitleBuildResult result)
        : base("Standard subtitles require review. Inspect the structured build warnings.") => Result = result;

    public StandardSubtitleBuildResult Result { get; }
}

/// <summary>
/// Builds initial source-grounded subtitles, not a Human transcript or an editorial change.
/// Splits only between real tokens; never interpolates, clips, or stretches their times.
/// Existing line breaks are preserved. Additional line breaks are not inserted into source text.
/// Font evidence is supplied by the caller; glyph fit and platform UI masks are not measured here.
/// </summary>
public static class StandardSubtitleBuilder
{
    public const string RuleVersion = "standard-subtitle-builder.exact-source-tokens.v2";

    /// <summary>Throws with a BuildResult if any source subtitle cannot be resolved safely.</summary>
    public static StandardSubtitleInput Build(
        CompositionVersion version, TimelineMap timeline, LayoutSpec layout, RenderProfile renderProfile,
        TranscriptRevision transcript, ContentDigest transcriptDigest, IReadOnlyList<SourceSpan> transcriptSpans,
        DefaultSettingsProfile settingsProfile, DefaultSettingsSnapshot settings, RenderFontEvidence font,
        string actorSid, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        StandardSubtitleBuildResult result = BuildWithEvidence(version, timeline, layout, renderProfile,
            transcript, transcriptDigest, transcriptSpans, settingsProfile, settings, font, actorSid,
            createdAt, cancellationToken);
        return result.HasUnresolvedCues ? throw new StandardSubtitleBuildException(result) : result.Subtitles;
    }

    /// <summary>
    /// Returns safe cues plus explicit exclusions and unmeasured-policy evidence. Empty ASR stays empty.
    /// transcriptSpans must include every original utterance span. Optional cut spans must be explicit,
    /// revision-bound, contiguous-token subsets of those spans; source times alone never prove identity.
    /// </summary>
    public static StandardSubtitleBuildResult BuildWithEvidence(
        CompositionVersion version, TimelineMap timeline, LayoutSpec layout, RenderProfile renderProfile,
        TranscriptRevision transcript, ContentDigest transcriptDigest, IReadOnlyList<SourceSpan> transcriptSpans,
        DefaultSettingsProfile settingsProfile, DefaultSettingsSnapshot settings, RenderFontEvidence font,
        string actorSid, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(renderProfile);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(transcriptDigest);
        ArgumentNullException.ThrowIfNull(transcriptSpans);
        ArgumentNullException.ThrowIfNull(settingsProfile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(font);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContext(version, timeline, layout, renderProfile, transcript, settingsProfile, settings,
            font, actorSid, createdAt);

        var spans = new Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>();
        foreach (SourceSpan span in transcriptSpans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (span is null || !spans.TryAdd(span.Id, span))
            {
                throw new SubtitleSegmentationException(SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
            }
        }

        SourceSpan[] originals = transcript.Utterances.Select(utterance =>
            spans.TryGetValue(utterance.SourceSpanId, out SourceSpan? span) ? span
                : throw new SubtitleSegmentationException(SubtitleSegmentationFailureReason.SourceSpanBindingMismatch)).ToArray();
        SubtitleSegmentationResult segmented = transcript.ChangeSource == TranscriptChangeSource.Machine
            ? SubtitleSegmenter.SegmentMachine(transcript, transcriptDigest, originals, cancellationToken)
            : SubtitleSegmenter.Segment(transcript, transcriptDigest, originals, cancellationToken);
        ValidateAdditionalSpans(transcript, originals, spans.Values, cancellationToken);

        var warnings = new List<StandardSubtitleBuildWarning>();
        Configuration configuration = Configure(version, timeline, layout, renderProfile, settingsProfile,
            settings, font, warnings);
        var histories = new List<SubtitleCueHistory>();
        cancellationToken.ThrowIfCancellationRequested();
        if (segmented.InspectedTokenCount == 0)
        {
            Warn(warnings, "subtitles.enabled", "empty-asr", "no-real-asr-cues", false);
            return Finish(histories, configuration, warnings, segmented, settings);
        }

        TextAtom[][] utterances = CreateAtoms(transcript, originals, segmented, cancellationToken);
        var cuts = new List<BoundCut>();
        for (int index = 0; index < timeline.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimelineMapEntry entry = timeline.Entries[index];
            if (entry.Origin.Kind != OriginKind.SourceSpan || entry.SourceRange is not TimeRange sourceRange)
            {
                continue; // Non-source cards have no ASR text to invent.
            }

            if (!spans.TryGetValue(entry.Origin.SourceSpanId!, out SourceSpan? span)
                || !Contains(span.Range, sourceRange))
            {
                Warn(warnings, "subtitles.sourceMapping", "source-span-binding",
                    "unresolved-output-source-span", true, sourceRange, entry.Origin.SourceSpanId);
                continue;
            }

            cuts.Add(new BoundCut(index, entry, span));
        }

        HashSet<EntityId<TranscriptTokenIdTag>> overlapping = segmented.TimingOverlaps.SelectMany(static overlap =>
            new[] { overlap.PreviousEvidenceTokenId, overlap.CurrentEvidenceTokenId }).ToHashSet();
        var eligible = new HashSet<EntityId<TranscriptTokenIdTag>>();
        foreach (TextAtom atom in utterances.SelectMany(static atoms => atoms))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!configuration.Enabled)
            {
                WarnAtom(warnings, atom, "subtitles.enabled", "disabled-by-settings", false);
                continue;
            }

            if (atom.Token.Range is not TimeRange range)
            {
                WarnAtom(warnings, atom, "subtitles.sourceTiming", "missing-token-timing", true);
                continue;
            }

            if (atom.HasUnmatchedText)
            {
                WarnAtom(warnings, atom, "subtitles.sourceTiming", "unmatched-text-without-token-timing", true);
                continue;
            }

            if (overlapping.Contains(atom.Token.Id))
            {
                WarnAtom(warnings, atom, "subtitles.sourceTiming", "overlapping-token-timing", true);
                continue;
            }

            BoundCut[] matching = cuts.Where(cut => SameSource(cut.Span, atom.Span)).ToArray();
            bool mapped = matching.Any(cut => Contains(cut.Entry.SourceRange!.Value, range));
            if (matching.Any(cut => Overlaps(cut.Entry.SourceRange!.Value, range)
                && !Contains(cut.Entry.SourceRange!.Value, range)))
            {
                WarnAtom(warnings, atom, "subtitles.sourceMapping", "cut-crosses-token-without-inner-timing", true);
            }

            if (!mapped)
            {
                // Deliberate output cuts are informational; they are not a claim that absent cues were evaluated.
                WarnAtom(warnings, atom, "subtitles.sourceMapping", "outside-output-cuts", false);
                continue;
            }

            if (range.Duration > configuration.MaximumDuration)
            {
                WarnAtom(warnings, atom, "subtitles.cueMaximumDuration", "token-too-long-without-inner-timing", true);
                continue;
            }

            TextMetrics metrics = Measure(atom.Utterance.Text[atom.TextStart..atom.TextEnd].Trim());
            if (!metrics.Valid || metrics.Lines > configuration.MaximumLines
                || metrics.LongestLineHalfWidthUnits > 2L * configuration.MaximumCharacters)
            {
                WarnAtom(warnings, atom, !metrics.Valid ? "subtitles.lineBreakPolicy"
                    : metrics.Lines > configuration.MaximumLines ? "subtitles.lineCountMaximum"
                    : configuration.LengthKey, !metrics.Valid ? "unsupported-source-display-text"
                    : "text-cannot-split-without-inner-token-timing", true);
                continue;
            }

            eligible.Add(atom.Token.Id);
        }

        var proposals = new List<MappedCandidate>();
        foreach (BoundCut cut in cuts)
        {
            foreach (TextAtom[] atoms in utterances)
            {
                var block = new List<TextAtom>();
                foreach (TextAtom atom in atoms)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool included = eligible.Contains(atom.Token.Id) && SameSource(atom.Span, cut.Span)
                        && atom.Token.Range is TimeRange range && Contains(cut.Entry.SourceRange!.Value, range);
                    bool longSilence = included && block.Count > 0
                        && atom.Token.Range!.Value.Start - block[^1].Token.Range!.Value.End >= new RationalTime(1, 2);
                    if (!included || longSilence)
                    {
                        Partition(block, cut, configuration, proposals, warnings, cancellationToken);
                        block.Clear();
                    }

                    if (included)
                    {
                        block.Add(atom);
                    }
                }

                Partition(block, cut, configuration, proposals, warnings, cancellationToken);
            }
        }

        MappedCandidate[] ordered = proposals.OrderBy(static candidate => candidate.OutputRange.Start)
            .ThenBy(static candidate => candidate.OutputRange.End)
            .ThenBy(static candidate => candidate.Candidate.Id.ToString(), StringComparer.Ordinal).ToArray();
        bool[] conflicts = FindOutputConflicts(ordered, configuration, warnings, cancellationToken);
        SubtitleLayoutRect? previousPlacement = null;
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MappedCandidate proposal = ordered[index];
            SubtitleCueCandidate candidate = proposal.Candidate;
            if (conflicts[index])
            {
                continue;
            }

            if (histories.Count >= StandardSubtitleInput.MaximumCueCount)
            {
                WarnCandidate(warnings, proposal, "subtitles.cueCount", "renderer-cue-limit", true);
                continue;
            }

            if (configuration.LayoutProfile is null)
            {
                WarnCandidate(warnings, proposal, "subtitles.layout", "no-versioned-safe-zone", true);
                continue;
            }

            SubtitleLayoutResolution resolution = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(
                candidate, layout, configuration.LayoutProfile, configuration.Placements, [], [], previousPlacement),
                cancellationToken);
            if (resolution.Status != SubtitleLayoutResolutionStatus.Resolved)
            {
                WarnCandidate(warnings, proposal, "subtitles.layout", "layout-not-resolved", true);
                continue;
            }

            StandardSubtitlePlacement.Validate(configuration.AssProfile, resolution);
            SubtitleCueHistory history = SubtitleService.CreateInitial(version, candidate, resolution,
                StableId<CueIdTag>(new { configuration.Digest, candidate.Id, kind = "cue" }),
                StableId<SubtitleCueRevisionIdTag>(new
                {
                    configuration.Digest,
                    candidate.Id,
                    kind = "revision",
                    actorSid,
                    createdAt = createdAt.ToUniversalTime()
                }),
                proposal.OutputRange, actorSid, createdAt,
                "Create source-grounded standard subtitles without changing transcript text or timing.", cancellationToken);
            histories.Add(history);
            previousPlacement = resolution.Plan!.Placement.Rect;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Finish(histories, configuration, warnings, segmented, settings);
    }

    private static StandardSubtitleBuildResult Finish(IReadOnlyList<SubtitleCueHistory> histories,
        Configuration configuration, IReadOnlyList<StandardSubtitleBuildWarning> warnings,
        SubtitleSegmentationResult segmented, DefaultSettingsSnapshot settings) =>
        new(new StandardSubtitleInput(histories, configuration.DisplayProfile, configuration.AssProfile, warnings: warnings),
            warnings, segmented.RuleVersion, settings.ContentDigest);

    private static void ValidateContext(CompositionVersion version, TimelineMap timeline, LayoutSpec layout,
        RenderProfile renderProfile, TranscriptRevision transcript, DefaultSettingsProfile profile,
        DefaultSettingsSnapshot settings, RenderFontEvidence font, string actorSid, DateTimeOffset createdAt)
    {
        if (settings.ProfileId != profile.Id || settings.ProfileVersion != profile.Version
            || settings.ProfileDigest != profile.ContentDigest)
        {
            throw new ArgumentException("The subtitle settings snapshot does not bind to its exact profile.", nameof(settings));
        }

        DefaultSettingsSnapshot restored = DefaultSettingsSnapshot.ParseCanonical(settings.CanonicalJson, profile);
        if (restored.ContentDigest != settings.ContentDigest
            || !string.Equals(restored.CanonicalJson, settings.CanonicalJson, StringComparison.Ordinal))
        {
            throw new ArgumentException("The subtitle settings snapshot is inconsistent.", nameof(settings));
        }

        if (version.ContentDigest != RenderGraphCanonicalEvidence.ComputeCompositionDigest(version.Elements)
            || timeline.CompositionVersionId != version.Id
            || !TimelineMapBuilder.Build(timeline.Id, version).Entries.SequenceEqual(timeline.Entries))
        {
            throw new ArgumentException("Subtitle output cuts must match the exact composition and timeline.", nameof(timeline));
        }

        if (renderProfile.LayoutSpecId != layout.Id
            || (BigInteger)renderProfile.Width * layout.OutputAspectHeight
                != (BigInteger)renderProfile.Height * layout.OutputAspectWidth)
        {
            throw new ArgumentException("The subtitle layout and render profile do not match.", nameof(renderProfile));
        }

        _ = TranscriptEditorContract.ValidateCanonicalSid(actorSid, nameof(actorSid));
        if (createdAt < version.CreatedAt || createdAt < transcript.CreatedAt)
        {
            throw new ArgumentException("Subtitle creation predates its source evidence.", nameof(createdAt));
        }

        if (!string.Equals(font.Name, "Noto Sans JP", StringComparison.Ordinal))
        {
            throw new ArgumentException("Standard subtitles require the supplied Noto Sans JP font evidence.", nameof(font));
        }
    }

    private static void ValidateAdditionalSpans(TranscriptRevision transcript, SourceSpan[] originals,
        IEnumerable<SourceSpan> supplied, CancellationToken cancellationToken)
    {
        var originalIds = originals.Select(static span => span.Id).ToHashSet();
        var tokens = transcript.Utterances.SelectMany(static utterance => utterance.Tokens)
            .ToDictionary(static token => token.Id);
        foreach (SourceSpan span in supplied.Where(span => !originalIds.Contains(span.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool grounded = span.TranscriptRevisionId == transcript.Id && span.TranscriptTokenIds.Count > 0
                && originals.Any(original => SameSource(original, span) && Contains(original.Range, span.Range)
                    && IsContiguous(original.TranscriptTokenIds, span.TranscriptTokenIds))
                && span.TranscriptTokenIds.All(id => tokens.TryGetValue(id, out Token? token)
                    && token.Range is TimeRange range && Contains(span.Range, range));
            if (!grounded)
            {
                throw new SubtitleSegmentationException(SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
            }
        }
    }

    private static bool IsContiguous(IReadOnlyList<EntityId<TranscriptTokenIdTag>> source,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> selected)
    {
        for (int start = 0; start <= source.Count - selected.Count; start++)
        {
            if (source.Skip(start).Take(selected.Count).SequenceEqual(selected))
            {
                return true;
            }
        }

        return false;
    }

    private static TextAtom[][] CreateAtoms(TranscriptRevision transcript, SourceSpan[] spans,
        SubtitleSegmentationResult segmented, CancellationToken cancellationToken)
    {
        var parents = segmented.Candidates.SelectMany(candidate => candidate.EvidenceTokenIds
            .Select(id => (Id: id, Candidate: candidate))).ToDictionary(static item => item.Id, static item => item.Candidate);
        var result = new TextAtom[transcript.Utterances.Count][];
        for (int index = 0; index < result.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Utterance utterance = transcript.Utterances[index];
            int[] starts = SubtitleSegmenter.FindUniqueOrderedTextStarts(utterance.Text,
                utterance.Tokens.Select(static token => token.Text).ToArray(), cancellationToken);
            HashSet<int> textBoundaries = StringInfo.ParseCombiningCharacters(utterance.Text)
                .Append(utterance.Text.Length).ToHashSet();
            result[index] = utterance.Tokens.Select((token, ordinal) =>
            {
                int start = ordinal == 0 ? 0 : starts[ordinal];
                int end = ordinal + 1 == starts.Length ? utterance.Text.Length : starts[ordinal + 1];
                bool unmatched = !OnlySeparators(utterance.Text[start..starts[ordinal]])
                    || !OnlySeparators(utterance.Text[(starts[ordinal] + token.Text.Length)..end]);
                return new TextAtom(token, utterance, spans[index], start, end, parents[token.Id],
                    textBoundaries.Contains(start), textBoundaries.Contains(end), unmatched);
            }).ToArray();
        }

        return result;
    }

    private static bool OnlySeparators(string text) => text.EnumerateRunes().All(static rune =>
        Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation);

    private static void Partition(IReadOnlyList<TextAtom> block, BoundCut cut, Configuration configuration,
        List<MappedCandidate> proposals, List<StandardSubtitleBuildWarning> warnings, CancellationToken cancellationToken)
    {
        if (block.Count == 0)
        {
            return;
        }

        // Backward feasibility avoids greedy overshoot and a final cue shorter than the minimum.
        // A partition is accepted only when EVERY token in this block is covered exactly once.
        int[] next = Enumerable.Repeat(-1, block.Count + 1).ToArray();
        var costs = new int[block.Count + 1];
        next[^1] = block.Count;
        for (int start = block.Count - 1; start >= 0; start--)
        {
            for (int end = start; end < block.Count; end++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeRange range = SourceRange(block, start, end);
                TextMetrics metrics = Measure(SourceText(block, start, end));
                if (range.Duration > configuration.MaximumDuration || metrics.Lines > configuration.MaximumLines
                    || metrics.LongestLineHalfWidthUnits > 2L * configuration.MaximumCharacters)
                {
                    break;
                }

                bool gapValid = end + 1 == block.Count || block[end + 1].Token.Range!.Value.Start
                    - block[end].Token.Range!.Value.End >= configuration.MinimumGap;
                if (next[end + 1] < 0 || !gapValid || !block[start].StartsAtTextBoundary
                    || !block[end].EndsAtTextBoundary || !Fits(metrics, range, configuration))
                {
                    continue;
                }

                int cost = checked(1 + costs[end + 1]);
                if (next[start] < 0 || cost <= costs[start])
                {
                    next[start] = end + 1; // Equal-cost ties choose the longest existing token prefix.
                    costs[start] = cost;
                }
            }
        }

        if (next[0] < 0)
        {
            TimeRange range = SourceRange(block, 0, block.Count - 1);
            TextMetrics metrics = Measure(SourceText(block, 0, block.Count - 1));
            int before = warnings.Count;
            void Exclude(string key, string reason) => Warn(warnings, key, "exact-token-partition", reason,
                true, range, block[0].Span.Id);
            if (!metrics.Valid)
                Exclude("subtitles.lineBreakPolicy", "unsupported-source-display-text");
            else if (metrics.ReadingCharacters == 0)
                Exclude("subtitles.lineBreakPolicy", "no-displayable-source-text");
            if (block.Any(static atom => !atom.StartsAtTextBoundary || !atom.EndsAtTextBoundary))
                Exclude("subtitles.lineBreakPolicy", "no-grapheme-safe-token-partition");
            if (metrics.LongestLineHalfWidthUnits > 2L * configuration.MaximumCharacters)
                Exclude(configuration.LengthKey, "line-too-long-without-feasible-token-partition");
            if (metrics.Lines > configuration.MaximumLines)
                Exclude("subtitles.lineCountMaximum", "too-many-source-lines");
            if (range.Duration > configuration.MaximumDuration)
                Exclude("subtitles.cueMaximumDuration", "duration-too-long-without-feasible-token-partition");
            if (range.Duration < configuration.MinimumDuration)
                Exclude("subtitles.cueMinimumDuration", "source-duration-below-minimum");
            if (!ReadingSpeedFits(metrics.ReadingCharacters, range.Duration, configuration.MaximumReadingSpeed))
                Exclude("subtitles.readingSpeedMaximum", "source-reading-speed-exceeded");
            if (Enumerable.Range(1, block.Count - 1).Any(index => block[index].Token.Range!.Value.Start
                - block[index - 1].Token.Range!.Value.End < configuration.MinimumGap))
                Exclude("subtitles.cueGapMinimum", "no-feasible-partition-with-source-gaps");
            if (warnings.Count == before)
                Exclude("subtitles.lineBreakPolicy", "no-feasible-exact-token-partition");
            return;
        }

        for (int start = 0; start < block.Count; start = next[start])
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = next[start] - 1;
            TextAtom first = block[start];
            TextAtom last = block[end];
            TimeRange range = SourceRange(block, start, end);
            string text = SourceText(block, start, end);
            if (text.Length != last.TextEnd - first.TextStart)
            {
                // Existing candidate trim convention only; never rewrite the transcript or its internal spaces.
                Warn(warnings, "subtitles.lineBreakPolicy", "source-text-boundary",
                    "cue-boundary-whitespace-trimmed", false, range, first.Span.Id);
            }

            EntityId<TranscriptTokenIdTag>[] ids = block.Skip(start).Take(end - start + 1)
                .Select(static atom => atom.Token.Id).ToArray();
            var identity = new
            {
                configuration.Digest,
                cut.Index,
                first.Span.AssetId,
                first.Span.StreamId,
                first.Span.SourceDigest,
                first.Parent.SourceRevisionId,
                first.Parent.SourceRevisionDigest,
                range,
                text,
                ids
            };
            var span = new SourceSpan(StableId<SourceSpanIdTag>(new { identity, kind = "source-span" }),
                first.Span.AssetId, first.Span.StreamId, range, first.Span.SourceDigest,
                first.Parent.SourceRevisionId, ids);
            bool atOriginalBoundary = last.Parent.EvidenceTokenIds[^1] == last.Token.Id;
            IReadOnlyList<SubtitleBoundaryReason> reasons = atOriginalBoundary
                ? last.Parent.BoundaryReasons : [SubtitleBoundaryReason.ExactTokenBoundary];
            var candidate = new SubtitleCueCandidate(StableId<SubtitleCueCandidateIdTag>(new { identity, kind = "candidate" }),
                first.Parent.SourceRevisionId, first.Parent.SourceRevisionDigest, first.Utterance.SpeakerId,
                text, span, OriginRef.FromSourceSpan(span.Id), ids, [], reasons,
                atOriginalBoundary ? last.Parent.ObservedPositiveGapAfter : null,
                "Source token boundaries retained by the standard subtitle settings policy.");
            var outputRange = new TimeRange(cut.Entry.OutputRange.Start
                + (range.Start - cut.Entry.SourceRange!.Value.Start), range.Duration);
            proposals.Add(new MappedCandidate(candidate, outputRange, first.Span));
        }
    }

    private static bool[] FindOutputConflicts(MappedCandidate[] ordered, Configuration configuration,
        List<StandardSubtitleBuildWarning> warnings, CancellationToken cancellationToken)
    {
        var conflicts = new bool[ordered.Length];
        var active = new List<int>();
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RationalTime earliestEnd = ordered[index].OutputRange.Start - configuration.MinimumGap;
            active.RemoveAll(previous => ordered[previous].OutputRange.End <= earliestEnd);
            foreach (int previous in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool overlaps = ordered[previous].OutputRange.End > ordered[index].OutputRange.Start;
                if (overlaps && !conflicts[previous])
                {
                    conflicts[previous] = true;
                    WarnCandidate(warnings, ordered[previous], "subtitles.sourceTiming", "output-cues-overlap", true);
                }

                if (!conflicts[index])
                {
                    conflicts[index] = true;
                    WarnCandidate(warnings, ordered[index], overlaps ? "subtitles.sourceTiming" : "subtitles.cueGapMinimum",
                        overlaps ? "output-cues-overlap" : "output-gap-below-minimum-without-retiming", true);
                }
            }

            active.Add(index);
        }

        return conflicts;
    }

    private static bool Fits(TextMetrics metrics, TimeRange range, Configuration configuration) =>
        metrics.Valid && metrics.ReadingCharacters > 0 && metrics.Lines <= configuration.MaximumLines
        && metrics.LongestLineHalfWidthUnits <= 2L * configuration.MaximumCharacters
        && range.Duration >= configuration.MinimumDuration
        && range.Duration <= configuration.MaximumDuration
        && ReadingSpeedFits(metrics.ReadingCharacters, range.Duration, configuration.MaximumReadingSpeed);

    private static bool ReadingSpeedFits(int characters, RationalTime duration, int maximum) =>
        (BigInteger)characters * duration.Rate <= (BigInteger)maximum * duration.Value;

    private static TextMetrics Measure(string source)
    {
        // Same four code points as SubtitleDisplayPolicy; only the DISPLAY is filtered.
        string display = source.Replace("、", string.Empty, StringComparison.Ordinal)
            .Replace("。", string.Empty, StringComparison.Ordinal).Replace("｡", string.Empty, StringComparison.Ordinal)
            .Replace("､", string.Empty, StringComparison.Ordinal).Trim();
        string[] lines = display.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        bool valid = !display.Any(static character => char.IsControl(character) && character is not ('\r' or '\n'))
            && (display.Length == 0 || lines.All(static line => !string.IsNullOrWhiteSpace(line)))
            // These literal source sequences are ASS commands, not safely escaped text.
            // Check after display filtering too: punctuation removal can join a backslash and N/n/h.
            && !display.Contains("\\N", StringComparison.Ordinal)
            && !display.Contains("\\n", StringComparison.Ordinal)
            && !display.Contains("\\h", StringComparison.Ordinal);
        int readingCharacters = 0;
        long longestLineHalfWidthUnits = 0;
        foreach (string line in lines)
        {
            int[] starts = StringInfo.ParseCombiningCharacters(line);
            // Research §4.5 / R17 II.6 applies the 0.5 rule to LINE LENGTH only.
            // CPS (II.18, CharactersPerSecond) retains one count per displayed grapheme.
            readingCharacters += starts.Length;
            long halfWidthUnits = 0;
            for (int index = 0; index < starts.Length; index++)
            {
                int end = index + 1 < starts.Length ? starts[index + 1] : line.Length;
                halfWidthUnits += MeasureGraphemeHalfWidthUnits(line.AsSpan(starts[index], end - starts[index]));
            }

            longestLineHalfWidthUnits = Math.Max(longestLineHalfWidthUnits, halfWidthUnits);
        }

        return new TextMetrics(valid, readingCharacters, longestLineHalfWidthUnits, lines.Length);
    }

    private static int MeasureGraphemeHalfWidthUnits(ReadOnlySpan<char> grapheme)
    {
        // Only printable ASCII and half-width katakana/punctuation use 0.5-character units.
        // Other graphemes (including combining sequences and emoji) remain one full character.
        // No normalization: width measurement must not rewrite or split the source grapheme.
        foreach (char character in grapheme)
        {
            if (character is not (>= '\u0020' and <= '\u007E' or >= '\uFF61' and <= '\uFF9F'))
                return 2;
        }

        // Half-width voiced/semi-voiced marks are spacing half-width characters even when
        // StringInfo groups them with the preceding kana (e.g. ｶﾞ = two half-width units).
        return grapheme.Length;
    }

    private static string SourceText(IReadOnlyList<TextAtom> block, int start, int end) =>
        block[start].Utterance.Text[block[start].TextStart..block[end].TextEnd].Trim();

    private static TimeRange SourceRange(IReadOnlyList<TextAtom> block, int start, int end) =>
        new(block[start].Token.Range!.Value.Start,
            block[end].Token.Range!.Value.End - block[start].Token.Range!.Value.Start);

    private static bool Contains(TimeRange outer, TimeRange inner) =>
        outer.Start <= inner.Start && inner.End <= outer.End;

    private static bool Overlaps(TimeRange left, TimeRange right) => left.Start < right.End && right.Start < left.End;

    private static bool SameSource(SourceSpan left, SourceSpan right) => left.AssetId == right.AssetId
        && left.SourceDigest == right.SourceDigest && left.TranscriptRevisionId == right.TranscriptRevisionId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal);

    private static Configuration Configure(CompositionVersion version, TimelineMap timeline, LayoutSpec layout,
        RenderProfile render, DefaultSettingsProfile profile, DefaultSettingsSnapshot settings,
        RenderFontEvidence font, List<StandardSubtitleBuildWarning> warnings)
    {
        var values = settings.Values.ToDictionary(static selection => selection.Key, static selection => selection.Value,
            StringComparer.Ordinal);
        DefaultSettingValue Get(string key, DefaultSettingValueKind kind, DefaultSettingUnit unit)
        {
            if (!values.TryGetValue(key, out DefaultSettingValue? value) || value.Kind != kind
                || profile.GetDefinition(key).Unit != unit)
                throw new ArgumentException($"Unsupported subtitle setting contract: {key}.", nameof(settings));
            return value;
        }

        int Integer(string key, DefaultSettingUnit unit, int minimum = 1)
        {
            long value = Get(key, DefaultSettingValueKind.Integer, unit).IntegerValue!.Value;
            return value >= minimum && value <= int.MaxValue ? (int)value
                : throw new ArgumentException($"Unsupported subtitle setting range: {key}.", nameof(settings));
        }

        RationalNumber Rational(string key, DefaultSettingUnit unit)
        {
            RationalNumber value = Get(key, DefaultSettingValueKind.Rational, unit).RationalValue!.Value;
            return value > default(RationalNumber) ? value
                : throw new ArgumentException($"Subtitle setting must be positive: {key}.", nameof(settings));
        }

        bool Boolean(string key) => Get(key, DefaultSettingValueKind.Boolean, DefaultSettingUnit.None).BooleanValue!.Value;
        void RequireText(string key, string expected)
        {
            if (!string.Equals(Get(key, DefaultSettingValueKind.Text, DefaultSettingUnit.None).TextValue, expected,
                StringComparison.Ordinal))
                throw new ArgumentException($"Unsupported subtitle setting policy: {key}.", nameof(settings));
        }

        bool vertical = render.Height > render.Width;
        string lengthKey = vertical ? "subtitles.verticalLineLengthMaximum" : "subtitles.horizontalLineLengthMaximum";
        string heightKey = vertical ? "subtitles.verticalLineHeight" : "subtitles.landscapeLineHeight";
        RequireText("subtitles.primaryProfile", "japaneseSdhOrientedCandidate");
        RequireText("subtitles.lineBreakPolicy", "naturalSyntacticBoundaries");
        RequireText("subtitles.cueGapPolicy", "twoFramesBelowHalfSecondOtherwisePreserve");
        RequireText("subtitles.foregroundColor", "#FFFFFF");
        RequireText("subtitles.backgroundColor", "#000000");
        RequireText(vertical ? "subtitles.verticalRegion" : "subtitles.landscapeRegion", vertical
            ? "central90PercentWidthBy75PercentHeight" : "central75PercentWidthBy90PercentHeight");
        RequireText("subtitles.semanticCompleteness", "NOT EVALUATED");
        RequireText("subtitles.platformCompliance", "notClaimed");
        RequireText("subtitles.platformUiMaskStatus", "unmeasured");
        RequireText("subtitles.platformUiMaskFallback", "bbcGenericSubtitleRegion");
        bool enabled = Boolean("subtitles.enabled");
        int characters = Integer(lengthKey, DefaultSettingUnit.FullWidthCharacters);
        int lines = Integer("subtitles.lineCountMaximum", DefaultSettingUnit.Count);
        int speed = Integer("subtitles.readingSpeedMaximum", DefaultSettingUnit.CharactersPerSecond);
        RationalNumber minimum = Rational("subtitles.cueMinimumDuration", DefaultSettingUnit.Seconds);
        var minimumDuration = new RationalTime(minimum.Numerator, minimum.Denominator);
        var maximumDuration = new RationalTime(Integer("subtitles.cueMaximumDuration", DefaultSettingUnit.Seconds), 1);
        RationalTime minimumGap = RationalTime.FromFrames(Integer("subtitles.cueGapMinimum", DefaultSettingUnit.Frames, 0),
            render.FrameRateNumerator, render.FrameRateDenominator);
        if (minimumDuration > maximumDuration)
            throw new ArgumentException("Subtitle minimum duration exceeds its maximum.", nameof(settings));

        RationalNumber lineHeight = Rational(heightKey, DefaultSettingUnit.Percent);
        BigInteger size = BigInteger.DivRem((BigInteger)render.Height * lineHeight.Numerator,
            (BigInteger)100 * lineHeight.Denominator, out BigInteger remainder);
        if (size < 1 || size > render.Height || size * lines > render.Height)
            throw new ArgumentException("Subtitle authored line height does not fit the output.", nameof(settings));
        int fontSize = (int)size;
        if (!remainder.IsZero)
            Warn(warnings, heightKey, "integer-ass-font-size", "line-height-rounded-inward-to-pixels", false);
        if (Boolean("subtitles.backgroundOpaque"))
            Warn(warnings, "subtitles.backgroundOpaque", "requested-transparent-black",
                "opaque-setting-explicitly-overridden-by-transparent-background-request", false);
        if (Boolean("subtitles.platformUiMaskWarningRequired"))
            Warn(warnings, "subtitles.platformUiMaskStatus", "generic-safe-region", "platform-ui-mask-not-measured", false);
        Warn(warnings, "subtitles.semanticCompleteness", "no-semantic-evaluator", "NOT EVALUATED", false);
        Warn(warnings, "subtitles.cueGapPolicy", "no-synthetic-timing",
            "source-gaps-preserved-instead-of-retimed", false);

        ContentDigest digest = Digest(new
        {
            rule = RuleVersion,
            version.Id,
            version.ContentDigest,
            timeline,
            layout,
            render,
            settings.ProfileId,
            settings.ProfileVersion,
            settings.ProfileDigest,
            settingsDigest = settings.ContentDigest,
            font,
            display = "full-hide-japanese-comma-period",
            background = "transparent-black",
            lineBreaks = "existing-only"
        });
        var displayProfile = new SubtitleDisplayProfile(digest, SubtitleCueDisplayMode.Full,
            SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod);
        var white = new AssColor(0, 255, 255, 255);
        var black = new AssColor(0, 0, 0, 0);
        var transparent = new AssColor(255, 0, 0, 0);
        var ass = new AssSubtitleProfile(render.Width, render.Height, Enum.GetValues<SubtitleFileCueStyle>()
            .Select(style => new AssStyleDefinition(style, font.Name, fontSize, white, white, black, transparent,
                false, false, false, false, 100, 100, 0, 0, AssBorderStyle.OutlineAndShadow, 0, 0,
                AssAlignment.BottomCenter, new AssMargins(0, 0, 0), 1)).ToArray());
        SubtitleLayoutProfile? layoutProfile = layout.SafeZones.Count == 0 ? null
            : new SubtitleLayoutProfile(layout.Id, digest, layout.SafeZones.Select(static zone => zone.Name).ToArray(),
                Enum.GetValues<SubtitleSoftConstraintKind>().Select(static kind => new SubtitleSoftConstraintWeight(kind, 0)).ToArray());
        SubtitlePlacementCandidate[] placements = CreatePlacements(layout, render, vertical,
            checked(fontSize * lines), fontSize, characters, digest);
        return new Configuration(enabled, characters, lines, speed, minimumDuration, maximumDuration, minimumGap,
            lengthKey, digest, displayProfile, ass, layoutProfile, placements);
    }

    private static SubtitlePlacementCandidate[] CreatePlacements(LayoutSpec layout, RenderProfile render,
        bool vertical, int height, int fontSize, int characters, ContentDigest digest)
    {
        int widthDivisor = vertical ? 20 : 8;
        int heightDivisor = vertical ? 8 : 20;
        int left = (int)(((long)render.Width + widthDivisor - 1) / widthDivisor);
        int right = (int)((long)render.Width * (widthDivisor - 1) / widthDivisor);
        int top = (int)(((long)render.Height + heightDivisor - 1) / heightDivisor);
        int bottom = (int)((long)render.Height * (heightDivisor - 1) / heightDivisor);
        foreach (SafeZone zone in layout.SafeZones)
            Intersect(zone.Rect, render, ref left, ref top, ref right, ref bottom);
        var placements = new List<SubtitlePlacementCandidate>();
        foreach (LayoutRegion region in layout.Regions.Where(static region => region.Role == LayoutRegionRole.Subtitle))
        {
            int x = left, y = top, farX = right, farY = bottom;
            Intersect(region.Rect, render, ref x, ref y, ref farX, ref farY);
            if (farY - (long)y < height || farX - (long)x < (long)fontSize * characters)
                continue;
            var rect = new SubtitleLayoutRect(new RationalNumber(x, render.Width),
                new RationalNumber(farY - height, render.Height), new RationalNumber(farX - x, render.Width),
                new RationalNumber(height, render.Height));
            placements.Add(new SubtitlePlacementCandidate(StableId<SubtitlePlacementCandidateIdTag>(
                new { digest, region.Name, rect }), region.Name, rect));
        }

        return placements.ToArray();
    }

    private static void Intersect(NormalizedRect rect, RenderProfile render,
        ref int left, ref int top, ref int right, ref int bottom)
    {
        left = Math.Max(left, Pixel(rect.X, default, render.Width, true));
        top = Math.Max(top, Pixel(rect.Y, default, render.Height, true));
        right = Math.Min(right, Pixel(rect.X, rect.Width, render.Width, false));
        bottom = Math.Min(bottom, Pixel(rect.Y, rect.Height, render.Height, false));
    }

    private static int Pixel(RationalNumber first, RationalNumber second, int extent, bool ceiling)
    {
        BigInteger numerator = ((BigInteger)first.Numerator * second.Denominator
            + (BigInteger)second.Numerator * first.Denominator) * extent;
        BigInteger value = BigInteger.DivRem(numerator, (BigInteger)first.Denominator * second.Denominator,
            out BigInteger remainder);
        return checked((int)(value + (ceiling && !remainder.IsZero ? BigInteger.One : BigInteger.Zero)));
    }

    private static ContentDigest Digest<T>(T value) => CanonicalJson.ComputeDigest(JsonSerializer.SerializeToUtf8Bytes(value));
    private static EntityId<T> StableId<T>(object value) =>
        EntityId<T>.From(new Guid(Convert.FromHexString(Digest(value).Hex)[..16]));

    private static void Warn(List<StandardSubtitleBuildWarning> warnings, string key, string rule, string reason,
        bool unresolved, TimeRange? range = null, EntityId<SourceSpanIdTag>? spanId = null) =>
        warnings.Add(new StandardSubtitleBuildWarning(key, RuleVersion + "." + rule, reason, unresolved, range, spanId));

    private static void WarnAtom(List<StandardSubtitleBuildWarning> warnings, TextAtom atom,
        string key, string reason, bool unresolved) => Warn(warnings, key, "source-token", reason,
            unresolved, atom.Token.Range ?? atom.Utterance.Range, atom.Span.Id);

    private static void WarnCandidate(List<StandardSubtitleBuildWarning> warnings, MappedCandidate candidate,
        string key, string reason, bool unresolved) => Warn(warnings, key, "output-cue", reason,
            unresolved, candidate.Candidate.SourceRange, candidate.OriginalSpan.Id);

    private sealed record TextAtom(Token Token, Utterance Utterance, SourceSpan Span,
        int TextStart, int TextEnd, SubtitleCueCandidate Parent,
        bool StartsAtTextBoundary, bool EndsAtTextBoundary, bool HasUnmatchedText);
    private sealed record BoundCut(int Index, TimelineMapEntry Entry, SourceSpan Span);
    private sealed record MappedCandidate(SubtitleCueCandidate Candidate, TimeRange OutputRange, SourceSpan OriginalSpan);
    private sealed record TextMetrics(bool Valid, int ReadingCharacters, long LongestLineHalfWidthUnits, int Lines);
    private sealed record Configuration(bool Enabled, int MaximumCharacters, int MaximumLines, int MaximumReadingSpeed,
        RationalTime MinimumDuration, RationalTime MaximumDuration, RationalTime MinimumGap, string LengthKey,
        ContentDigest Digest, SubtitleDisplayProfile DisplayProfile, AssSubtitleProfile AssProfile,
        SubtitleLayoutProfile? LayoutProfile, SubtitlePlacementCandidate[] Placements);
}