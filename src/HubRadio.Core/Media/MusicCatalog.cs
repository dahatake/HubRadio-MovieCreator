using System.Globalization;
using System.Text;
using HubRadio.Core.Identity;

namespace HubRadio.Core.Media;

public enum MusicLibraryState
{
    Available,
    Absent,
    Empty,
    Inaccessible,
    Unsafe,
    NoEligibleTracks,
}

public enum MusicCatalogWarning
{
    LibraryAbsent,
    LibraryEmpty,
    LibraryInaccessible,
    UnsafeLibraryLocation,
    TrackUnreadable,
    DuplicateContent,
    SourceChangedDuringScan,
    NoEligibleTracks,
}

public sealed class MusicTrack
{
    public MusicTrack(
        ContentDigest contentDigest,
        long byteSize,
        decimal durationSeconds,
        string codecName,
        int sampleRate,
        int channels)
    {
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(codecName);

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteSize));
        }

        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        if (string.IsNullOrWhiteSpace(codecName) ||
            !string.Equals(codecName, codecName.Trim(), StringComparison.Ordinal) ||
            codecName.Any(char.IsControl) ||
            !string.Equals(codecName, "mp3", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The codec name must identify canonical MP3 technical metadata.",
                nameof(codecName));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        ContentDigest = contentDigest;
        ByteSize = byteSize;
        DurationSeconds = durationSeconds;
        CodecName = codecName;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public decimal DurationSeconds { get; }

    public string CodecName { get; }

    public int SampleRate { get; }

    public int Channels { get; }
}

public sealed class MusicCatalog
{
    private MusicCatalog(
        MusicLibraryState state,
        IReadOnlyList<MusicTrack> tracks,
        IReadOnlyList<MusicCatalogWarning> warnings)
    {
        State = state;
        Tracks = tracks;
        Warnings = warnings;
        CatalogDigest = ComputeDigest(state, tracks, warnings);
    }

    public MusicLibraryState State { get; }

    public ContentDigest CatalogDigest { get; }

    public IReadOnlyList<MusicTrack> Tracks { get; }

    public IReadOnlyList<MusicCatalogWarning> Warnings { get; }

    public static MusicCatalog CreateAvailable(
        IReadOnlyList<MusicTrack> tracks,
        IReadOnlyList<MusicCatalogWarning>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        warnings ??= [];

        MusicTrack[] ordered = CopyTracks(tracks);
        if (ordered.Length == 0)
        {
            throw new ArgumentException(
                "An available music catalog must contain at least one track.",
                nameof(tracks));
        }

        IReadOnlyList<MusicCatalogWarning> copiedWarnings = CopyWarnings(warnings);
        if (copiedWarnings.Any(static warning => warning is
                MusicCatalogWarning.LibraryAbsent or
                MusicCatalogWarning.LibraryEmpty or
                MusicCatalogWarning.LibraryInaccessible or
                MusicCatalogWarning.NoEligibleTracks))
        {
            throw new ArgumentException(
                "An available music catalog cannot carry an unavailable-state warning.",
                nameof(warnings));
        }

        return new MusicCatalog(
            MusicLibraryState.Available,
            Array.AsReadOnly(ordered),
            copiedWarnings);
    }

    public static MusicCatalog CreateUnavailable(
        MusicLibraryState state,
        params MusicCatalogWarning[] warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (state == MusicLibraryState.Available)
        {
            throw new ArgumentException(
                "An unavailable catalog cannot use the available state.",
                nameof(state));
        }

        IReadOnlyList<MusicCatalogWarning> copiedWarnings = CopyWarnings(warnings);
        ValidateUnavailableWarnings(state, copiedWarnings, nameof(warnings));
        return new MusicCatalog(
            state,
            Array.AsReadOnly(Array.Empty<MusicTrack>()),
            copiedWarnings);
    }

    private static MusicTrack[] CopyTracks(IReadOnlyList<MusicTrack> tracks)
    {
        MusicTrack[] copy = new MusicTrack[tracks.Count];
        HashSet<string> digests = new(StringComparer.Ordinal);
        for (int index = 0; index < copy.Length; index++)
        {
            MusicTrack? track = tracks[index];
            if (track is null)
            {
                throw new ArgumentException(
                    "Music catalog tracks must not contain null entries.",
                    nameof(tracks));
            }

            if (!digests.Add(track.ContentDigest.Hex))
            {
                throw new ArgumentException(
                    "Music catalog tracks must have unique content digests.",
                    nameof(tracks));
            }

            copy[index] = track;
        }

        Array.Sort(
            copy,
            static (left, right) => StringComparer.Ordinal.Compare(
                left.ContentDigest.Hex,
                right.ContentDigest.Hex));
        return copy;
    }

    private static IReadOnlyList<MusicCatalogWarning> CopyWarnings(
        IReadOnlyList<MusicCatalogWarning> warnings)
    {
        SortedSet<MusicCatalogWarning> normalized = [];
        foreach (MusicCatalogWarning warning in warnings)
        {
            if (!Enum.IsDefined(warning))
            {
                throw new ArgumentOutOfRangeException(nameof(warnings));
            }

            normalized.Add(warning);
        }

        return Array.AsReadOnly(normalized.ToArray());
    }

    private static void ValidateUnavailableWarnings(
        MusicLibraryState state,
        IReadOnlyList<MusicCatalogWarning> warnings,
        string parameterName)
    {
        MusicCatalogWarning? exclusiveWarning = state switch
        {
            MusicLibraryState.Absent => MusicCatalogWarning.LibraryAbsent,
            MusicLibraryState.Empty => MusicCatalogWarning.LibraryEmpty,
            MusicLibraryState.Inaccessible => MusicCatalogWarning.LibraryInaccessible,
            MusicLibraryState.Unsafe => MusicCatalogWarning.UnsafeLibraryLocation,
            MusicLibraryState.NoEligibleTracks => null,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        bool valid = exclusiveWarning is not null
            ? warnings.Count == 1 && warnings[0] == exclusiveWarning.Value
            : warnings.Contains(MusicCatalogWarning.NoEligibleTracks) &&
                !warnings.Any(static warning => warning is
                    MusicCatalogWarning.LibraryAbsent or
                    MusicCatalogWarning.LibraryEmpty or
                    MusicCatalogWarning.LibraryInaccessible);
        if (!valid)
        {
            throw new ArgumentException(
                "Music catalog warnings must agree with the unavailable state.",
                parameterName);
        }
    }

    private static ContentDigest ComputeDigest(
        MusicLibraryState state,
        IReadOnlyList<MusicTrack> tracks,
        IReadOnlyList<MusicCatalogWarning> warnings)
    {
        StringBuilder payload = new();
        payload.Append("music-catalog-v1\nstate=");
        payload.Append(state);
        foreach (MusicCatalogWarning warning in warnings)
        {
            payload.Append("\nwarning=");
            payload.Append(warning);
        }

        foreach (MusicTrack track in tracks)
        {
            payload.Append("\ntrack=");
            payload.Append(track.ContentDigest.Hex);
            payload.Append('\t');
            payload.Append(track.ByteSize.ToString(CultureInfo.InvariantCulture));
            payload.Append('\t');
            payload.Append(track.DurationSeconds.ToString(
                "G29",
                CultureInfo.InvariantCulture));
            payload.Append('\t');
            payload.Append(track.CodecName);
            payload.Append('\t');
            payload.Append(track.SampleRate.ToString(CultureInfo.InvariantCulture));
            payload.Append('\t');
            payload.Append(track.Channels.ToString(CultureInfo.InvariantCulture));
        }

        return ContentDigest.Compute(Encoding.UTF8.GetBytes(payload.ToString()));
    }
}