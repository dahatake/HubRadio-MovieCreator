using System.Text;
using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Rendering;

public sealed record StandardMusicCardBinding(EntityId<LongFormElementIdTag> ElementId, MusicCue Cue);

public sealed record StandardMusicRenderEvidence(EntityId<LongFormElementIdTag> ElementId,
    ContentDigest CueDigest, ContentDigest CatalogDigest, ContentDigest TrackDigest, long TrackByteSize,
    ContentDigest TranscriptDigest, TimeRange OutputRange, MusicCueKind Kind,
    string SpeechEvidence = "same-revision-transcript-no-overlap", bool Loop = false, bool Stretch = false);

/// <summary>
/// Optional actual card soundtrack inputs. Track paths never enter plan text or
/// filter expressions. A missing library is handled by the planning policy, not
/// by fabricating an opener here. Musical fit/legal clearance remain unevaluated.
/// </summary>
public sealed class StandardMusicRenderInput
{
    public StandardMusicRenderInput(MusicCatalog catalog, TranscriptRevision transcript,
        IReadOnlyList<StandardMusicCardBinding> cards, IReadOnlyList<BinaryVerificationRequest> tracks)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(tracks);
        if (cards.Count > 16 || tracks.Count > 16 || cards.Any(static item => item is null || item.Cue is null || item.ElementId is null)
            || tracks.Any(static item => item is null)
            || cards.Select(static item => item.Cue.CueDigest).Distinct().Count() != cards.Count
            || tracks.Select(static item => item.ExpectedDigest).Distinct().Count() != tracks.Count)
            throw new ArgumentException("Music inputs must be bounded and unique.");
        Cards = Array.AsReadOnly(cards.ToArray());
        Tracks = Array.AsReadOnly(tracks.ToArray());
        if (Tracks.Count > 0)
        {
            string? directory = Path.GetDirectoryName(Tracks[0].FilePath);
            if (directory is null || !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(directory), "Music", StringComparison.OrdinalIgnoreCase)
                || Tracks.Any(track => !string.Equals(Path.GetDirectoryName(track.FilePath), directory, WorkspacePathSafety.PathComparison)))
                throw new ArgumentException("Music tracks must come from one local read-only Music library.", nameof(tracks));
            LibraryRoot = directory;
        }
    }

    public MusicCatalog Catalog { get; }
    public TranscriptRevision Transcript { get; }
    public IReadOnlyList<StandardMusicCardBinding> Cards { get; }
    public IReadOnlyList<BinaryVerificationRequest> Tracks { get; }
    public string? LibraryRoot { get; }

    internal async Task<bool> VerifyCatalogAsync(string ffprobePath, ProcessRunner runner, TimeSpan timeout, CancellationToken token)
    {
        if (Tracks.Count == 0) return true;
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(Path.GetDirectoryName(ffprobePath)!);
        WorkspacePathSafety.ValidateRegularFileAttributes(File.GetAttributes(ffprobePath));
        using var probeLease = new FileStream(ffprobePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        MusicCatalog actual = await new MusicCatalogScanner(LibraryRoot!, runner, new BinaryVerifier(), ffprobePath, timeout)
            .ScanAsync(token).ConfigureAwait(false);
        if (actual.State != MusicLibraryState.Available) return false;
        if (actual.CatalogDigest != Catalog.CatalogDigest)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
        return true;
    }

    public static MusicCue CreateCue(EntityId<LongFormElementIdTag> elementId, MusicCueKind kind,
        TimeRange outputRange, bool hasForegroundSpeech)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        if (kind is not (MusicCueKind.TitleCard or MusicCueKind.TopicCard or MusicCueKind.EndCard)
            || outputRange.Start < default(RationalTime) || outputRange.Duration <= default(RationalTime))
            throw new ArgumentException("Music requires an explicit card range.");
        ContentDigest digest = StandardRenderGraphFactory.Digest(new
        {
            domain = "standard-explicit-card-music-v1",
            elementId,
            kind,
            outputRange,
            hasForegroundSpeech,
        });
        return new MusicCue(digest, kind, (decimal)outputRange.Duration.Value / outputRange.Duration.Rate, hasForegroundSpeech);
    }

    internal IReadOnlyList<(FfmpegMusicBinding Binding, StandardMusicRenderEvidence Evidence)> Resolve(
        StandardCompositionPreparation preparation)
    {
        StandardCompositionPlan plan = preparation.Plan;
        ContentDigest transcriptDigest = ContentDigest.Compute(Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(Transcript)));
        if (Catalog.CatalogDigest != plan.MusicPlanning.CatalogDigest
            || Transcript.Id != plan.LongForm.Provenance.TranscriptRevisionId
            || transcriptDigest != plan.LongForm.Provenance.TranscriptRevisionDigest)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
        MusicCueAssignment[] assignments = plan.MusicPlanning.Assignments.Where(static item => item.TrackDigest is not null).ToArray();
        if (assignments.Length != Cards.Count || assignments.Select(static item => item.TrackDigest).Distinct().Count() != Tracks.Count)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
        if (assignments.Length == 0) return [];
        if (Catalog.State != MusicLibraryState.Available)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
        var resolved = new List<(FfmpegMusicBinding, StandardMusicRenderEvidence)>();
        foreach (MusicCueAssignment assignment in assignments)
        {
            StandardMusicCardBinding? card = Cards.SingleOrDefault(item => item.Cue.CueDigest == assignment.CueDigest);
            LongFormElementPlan? element = plan.LongForm.Elements.SingleOrDefault(item => item.Id == card?.ElementId);
            MusicTrack? track = Catalog.Tracks.SingleOrDefault(item => item.ContentDigest == assignment.TrackDigest);
            BinaryVerificationRequest? file = Tracks.SingleOrDefault(item => item.ExpectedDigest == assignment.TrackDigest);
            if (card is null || element?.State != LongFormElementState.Included || element.OutputRange is not TimeRange range
                || element.Text is null || range.End > preparation.ChildTimelineMap.Entries[^1].OutputRange.End
                || track is null || file is null || track.ByteSize != file.ExpectedByteSize
                || !string.Equals(Path.GetExtension(file.FilePath), ".mp3", StringComparison.OrdinalIgnoreCase)
                || assignment.Reason != MusicAssignmentReason.Assigned || assignment.Loop || assignment.Stretch
                || assignment.RelativeGainDecibels is not null || card.Cue.HasForegroundSpeech)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
            MusicCueKind kind = element.Kind switch
            {
                LongFormElementKind.EpisodeTitle => MusicCueKind.TitleCard,
                LongFormElementKind.Topic => MusicCueKind.TopicCard,
                LongFormElementKind.Ending => MusicCueKind.EndCard,
                _ => throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic),
            };
            MusicCue expected = CreateCue(element.Id, kind, range, false);
            if (expected.CueDigest != card.Cue.CueDigest || card.Cue.Kind != kind || card.Cue.DurationSeconds != expected.DurationSeconds
                || !MusicPolicy.Evaluate(kind, false, expected.DurationSeconds, track.DurationSeconds, MusicPlanningOptions.Default).IsEligible
                || !FfmpegCompiler.IsOnOutputGrid(range.Start, plan.LongForm.Profile.Profile)
                || !FfmpegCompiler.IsOnOutputGrid(range.Duration, plan.LongForm.Profile.Profile))
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
            RationalTime covered = default;
            foreach (TimelineMapEntry entry in preparation.ChildTimelineMap.Entries.Where(entry =>
                entry.OutputRange.Start < range.End && range.Start < entry.OutputRange.End))
            {
                RationalTime start = range.Start > entry.OutputRange.Start ? range.Start : entry.OutputRange.Start;
                RationalTime end = range.End < entry.OutputRange.End ? range.End : entry.OutputRange.End;
                if (entry.SourceRange is not TimeRange sourceRange)
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
                RationalTime sourceStart = sourceRange.Start + start - entry.OutputRange.Start;
                RationalTime sourceEnd = sourceStart + end - start;
                covered += end - start;
                if (Transcript.Utterances.Any(utterance => utterance.Range.Start < sourceEnd && sourceStart < utterance.Range.End))
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
            }
            if (covered != range.Duration || resolved.Any(item => item.Item2.OutputRange.Start < range.End && range.Start < item.Item2.OutputRange.End))
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
            resolved.Add((new FfmpegMusicBinding(file, range), new StandardMusicRenderEvidence(element.Id, assignment.CueDigest,
                Catalog.CatalogDigest, track.ContentDigest, track.ByteSize, transcriptDigest, range, kind)));
        }
        return resolved.AsReadOnly();
    }
}