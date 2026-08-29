using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Media;

public sealed class ProxyFrameMapEntry
{
    internal ProxyFrameMapEntry(
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        int sourceFrameSequence,
        RationalTime sourcePresentationTime)
    {
        ProxyFrameSequence = proxyFrameSequence;
        ProxyPresentationTime = proxyPresentationTime;
        SourceFrameSequence = sourceFrameSequence;
        SourcePresentationTime = sourcePresentationTime;
    }

    public int ProxyFrameSequence { get; }

    public RationalTime ProxyPresentationTime { get; }

    public int SourceFrameSequence { get; }

    public RationalTime SourcePresentationTime { get; }
}

public sealed class ProxyFrameMapRoundTripEvidence
{
    internal ProxyFrameMapRoundTripEvidence(
        int testedSourceFrameCount,
        int unrepresentableSourceFrameCount,
        RationalTime maximumError)
    {
        if (testedSourceFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(testedSourceFrameCount),
                "Round-trip evidence must test at least one source frame.");
        }

        if (unrepresentableSourceFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unrepresentableSourceFrameCount),
                "The unrepresentable source-frame count must not be negative.");
        }

        if (maximumError < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumError),
                "The maximum round-trip error must not be negative.");
        }

        TestedSourceFrameCount = testedSourceFrameCount;
        UnrepresentableSourceFrameCount = unrepresentableSourceFrameCount;
        MaximumError = maximumError;
    }

    public int TestedSourceFrameCount { get; }

    public int UnrepresentableSourceFrameCount { get; }

    public RationalTime MaximumError { get; }
}

public sealed class ProxyFrameMap
{
    public const string CurrentSchemaVersion = "1.0.0";

    private ProxyFrameMap(
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        ContentDigest sourceContentDigest,
        ContentDigest sourcePtsIndexDigest,
        ContentDigest proxyContentDigest,
        ContentDigest proxyPtsIndexDigest,
        int sourceFrameCount,
        RationalTime sourcePresentationStart,
        RationalTime sourcePresentationEnd,
        IReadOnlyList<ProxyFrameMapEntry> entries,
        ProxyFrameMapRoundTripEvidence roundTrip)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(sourceContentDigest);
        ArgumentNullException.ThrowIfNull(sourcePtsIndexDigest);
        ArgumentNullException.ThrowIfNull(proxyContentDigest);
        ArgumentNullException.ThrowIfNull(proxyPtsIndexDigest);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(roundTrip);

        if (sourceFrameCount <= 0 ||
            sourcePresentationEnd < sourcePresentationStart ||
            roundTrip.TestedSourceFrameCount +
                roundTrip.UnrepresentableSourceFrameCount != sourceFrameCount)
        {
            throw new ArgumentException(
                "Proxy frame-map source counts are inconsistent.",
                nameof(sourceFrameCount));
        }

        ProxyFrameMapEntry[] copiedEntries = new ProxyFrameMapEntry[entries.Count];
        ProxyFrameMapEntry? previous = null;
        for (int index = 0; index < copiedEntries.Length; index++)
        {
            ProxyFrameMapEntry entry = entries[index]
                ?? throw new ArgumentException(
                    "Proxy frame-map entries must not contain null values.",
                    nameof(entries));
            if (entry.ProxyFrameSequence != index ||
                entry.SourceFrameSequence < 0 ||
                entry.SourceFrameSequence >= sourceFrameCount ||
                (previous is not null &&
                    (entry.ProxyPresentationTime <= previous.ProxyPresentationTime ||
                        entry.SourceFrameSequence < previous.SourceFrameSequence ||
                        entry.SourcePresentationTime < previous.SourcePresentationTime ||
                        (entry.SourceFrameSequence == previous.SourceFrameSequence &&
                            entry.SourcePresentationTime !=
                                previous.SourcePresentationTime) ||
                        (entry.SourceFrameSequence > previous.SourceFrameSequence &&
                            entry.SourcePresentationTime <=
                                previous.SourcePresentationTime))))
            {
                throw new ArgumentException(
                    "Proxy frame-map entries are inconsistent.",
                    nameof(entries));
            }

            copiedEntries[index] = entry;
            previous = entry;
        }

        if (copiedEntries.Length == 0)
        {
            throw new ArgumentException(
                "A proxy frame map must contain at least one proxy frame.",
                nameof(entries));
        }

        SourceAssetId = sourceAssetId;
        ProjectId = projectId;
        SourceContentDigest = sourceContentDigest;
        SourcePtsIndexDigest = sourcePtsIndexDigest;
        ProxyContentDigest = proxyContentDigest;
        ProxyPtsIndexDigest = proxyPtsIndexDigest;
        SourceFrameCount = sourceFrameCount;
        SourcePresentationStart = sourcePresentationStart;
        SourcePresentationEnd = sourcePresentationEnd;
        Entries = Array.AsReadOnly(copiedEntries);
        RoundTrip = roundTrip;
        CanonicalJson = BuildCanonicalJson();
    }

    public string SchemaVersion => CurrentSchemaVersion;

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ContentDigest SourceContentDigest { get; }

    public ContentDigest SourcePtsIndexDigest { get; }

    public ContentDigest ProxyContentDigest { get; }

    public ContentDigest ProxyPtsIndexDigest { get; }

    public int SourceFrameCount { get; }

    public RationalTime SourcePresentationStart { get; }

    public RationalTime SourcePresentationEnd { get; }

    public IReadOnlyList<ProxyFrameMapEntry> Entries { get; }

    public ProxyFrameMapRoundTripEvidence RoundTrip { get; }

    public string CanonicalJson { get; }

    public static ProxyFrameMap Create(
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        ContentDigest sourceContentDigest,
        ContentDigest sourcePtsIndexDigest,
        ContentDigest proxyContentDigest,
        ContentDigest proxyPtsIndexDigest,
        IReadOnlyList<RationalTime> sourcePresentationTimes,
        IReadOnlyList<RationalTime> proxyPresentationTimes)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(sourceContentDigest);
        ArgumentNullException.ThrowIfNull(sourcePtsIndexDigest);
        ArgumentNullException.ThrowIfNull(proxyContentDigest);
        ArgumentNullException.ThrowIfNull(proxyPtsIndexDigest);
        ArgumentNullException.ThrowIfNull(sourcePresentationTimes);
        ArgumentNullException.ThrowIfNull(proxyPresentationTimes);

        RationalTime[] sourceTimes = CopyStrictlyIncreasing(
            sourcePresentationTimes,
            nameof(sourcePresentationTimes));
        RationalTime[] proxyTimes = CopyStrictlyIncreasing(
            proxyPresentationTimes,
            nameof(proxyPresentationTimes));
        if (proxyTimes[^1] < sourceTimes[0] || sourceTimes[^1] < proxyTimes[0])
        {
            throw new ArgumentException(
                "Source and proxy presentation timestamps must overlap.",
                nameof(proxyPresentationTimes));
        }

        ProxyFrameMapEntry[] entries = BuildEntries(sourceTimes, proxyTimes);
        ProxyFrameMapRoundTripEvidence roundTrip = VerifyRoundTrip(
            sourceTimes,
            entries);
        return new ProxyFrameMap(
            sourceAssetId,
            projectId,
            sourceContentDigest,
            sourcePtsIndexDigest,
            proxyContentDigest,
            proxyPtsIndexDigest,
            sourceTimes.Length,
            sourceTimes[0],
            sourceTimes[^1],
            entries,
            roundTrip);
    }

    public ProxyFrameMapEntry MapProxyFrameToSource(int proxyFrameSequence)
    {
        if ((uint)proxyFrameSequence >= (uint)Entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(proxyFrameSequence),
                "The proxy frame sequence is outside the frame map.");
        }

        return Entries[proxyFrameSequence];
    }

    public bool TryMapSourceTimeToProxyFrame(
        RationalTime sourceTime,
        [NotNullWhen(true)] out ProxyFrameMapEntry? proxyFrame)
    {
        if (sourceTime < SourcePresentationStart ||
            sourceTime > SourcePresentationEnd)
        {
            proxyFrame = null;
            return false;
        }

        int low = 0;
        int high = Entries.Count - 1;
        int matchedIndex = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ProxyFrameMapEntry candidate = Entries[middle];
            if (candidate.ProxyPresentationTime <= sourceTime)
            {
                matchedIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (matchedIndex < 0)
        {
            proxyFrame = null;
            return false;
        }

        proxyFrame = Entries[matchedIndex];
        return true;
    }

    private static RationalTime[] CopyStrictlyIncreasing(
        IReadOnlyList<RationalTime> source,
        string parameterName)
    {
        if (source.Count == 0)
        {
            throw new ArgumentException(
                "Observed presentation timestamps must not be empty.",
                parameterName);
        }

        var copy = new RationalTime[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            RationalTime value = source[index];
            if (index > 0 && value <= copy[index - 1])
            {
                throw new ArgumentException(
                    "Observed presentation timestamps must be strictly increasing.",
                    parameterName);
            }

            copy[index] = value;
        }

        return copy;
    }

    private static ProxyFrameMapEntry[] BuildEntries(
        IReadOnlyList<RationalTime> sourceTimes,
        IReadOnlyList<RationalTime> proxyTimes)
    {
        var entries = new ProxyFrameMapEntry[proxyTimes.Count];
        int sourceIndex = 0;
        for (int proxyIndex = 0; proxyIndex < proxyTimes.Count; proxyIndex++)
        {
            RationalTime proxyTime = proxyTimes[proxyIndex];
            while (sourceIndex + 1 < sourceTimes.Count &&
                sourceTimes[sourceIndex + 1] <= proxyTime)
            {
                sourceIndex++;
            }

            entries[proxyIndex] = new ProxyFrameMapEntry(
                proxyIndex,
                proxyTime,
                sourceIndex,
                sourceTimes[sourceIndex]);
        }

        return entries;
    }

    private static ProxyFrameMapRoundTripEvidence VerifyRoundTrip(
        IReadOnlyList<RationalTime> sourceTimes,
        IReadOnlyList<ProxyFrameMapEntry> entries)
    {
        int proxyIndex = -1;
        int testedCount = 0;
        int unrepresentableCount = 0;
        RationalTime maximumError = default;
        for (int sourceIndex = 0; sourceIndex < sourceTimes.Count; sourceIndex++)
        {
            RationalTime sourceTime = sourceTimes[sourceIndex];
            while (proxyIndex + 1 < entries.Count &&
                entries[proxyIndex + 1].ProxyPresentationTime <= sourceTime)
            {
                proxyIndex++;
            }

            if (proxyIndex < 0)
            {
                unrepresentableCount++;
                continue;
            }

            RationalTime recoveredSourceTime =
                entries[proxyIndex].SourcePresentationTime;
            RationalTime error = sourceTime - recoveredSourceTime;
            if (error < default(RationalTime))
            {
                throw new ArgumentException(
                    "The observed timestamps do not produce a valid round trip.",
                    nameof(entries));
            }

            if (error > maximumError)
            {
                maximumError = error;
            }

            testedCount++;
        }

        return new ProxyFrameMapRoundTripEvidence(
            testedCount,
            unrepresentableCount,
            maximumError);
    }

    private string BuildCanonicalJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SchemaVersion);
            writer.WriteString("sourceAssetId", SourceAssetId.ToString());
            writer.WriteString("projectId", ProjectId.ToString());
            writer.WriteString("sourceContentDigest", SourceContentDigest.Hex);
            writer.WriteString("sourcePtsIndexDigest", SourcePtsIndexDigest.Hex);
            writer.WriteString("proxyContentDigest", ProxyContentDigest.Hex);
            writer.WriteString("proxyPtsIndexDigest", ProxyPtsIndexDigest.Hex);
            writer.WriteNumber("sourceFrameCount", SourceFrameCount);
            writer.WritePropertyName("sourcePresentationStart");
            WriteRationalTime(writer, SourcePresentationStart);
            writer.WritePropertyName("sourcePresentationEnd");
            WriteRationalTime(writer, SourcePresentationEnd);
            writer.WritePropertyName("roundTrip");
            writer.WriteStartObject();
            writer.WriteNumber(
                "testedSourceFrameCount",
                RoundTrip.TestedSourceFrameCount);
            writer.WriteNumber(
                "unrepresentableSourceFrameCount",
                RoundTrip.UnrepresentableSourceFrameCount);
            writer.WritePropertyName("maximumError");
            WriteRationalTime(writer, RoundTrip.MaximumError);
            writer.WriteEndObject();
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (ProxyFrameMapEntry entry in Entries)
            {
                writer.WriteStartObject();
                writer.WriteNumber(
                    "proxyFrameSequence",
                    entry.ProxyFrameSequence);
                writer.WritePropertyName("proxyPresentationTime");
                WriteRationalTime(writer, entry.ProxyPresentationTime);
                writer.WriteNumber(
                    "sourceFrameSequence",
                    entry.SourceFrameSequence);
                writer.WritePropertyName("sourcePresentationTime");
                WriteRationalTime(writer, entry.SourcePresentationTime);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] canonical =
            HubRadio.Core.Serialization.CanonicalJson.CanonicalizeToUtf8(
                buffer.WrittenSpan);
        return new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(canonical);
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteNumber("rate", value.Rate);
        writer.WriteEndObject();
    }
}