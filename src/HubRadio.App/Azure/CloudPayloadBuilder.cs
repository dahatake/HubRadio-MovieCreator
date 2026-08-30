using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HubRadio.App.Policy;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Terms;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Azure;

internal enum CloudPayloadPurpose
{
    TextSuggestionEvaluation,
}

internal enum CloudPayloadSegmentRole
{
    ContextBefore,
    Selected,
    ContextAfter,
}

internal enum CloudPayloadFailureReason
{
    InvalidSelection,
    ContextNotAdjacent,
    OverlappingSelection,
    SensitivityScanIncomplete,
    ConfirmationDigestMismatch,
    ConfirmationUnavailable,
    PayloadIntegrityMismatch,
}

internal sealed class CloudPayloadException : Exception
{
    internal CloudPayloadException(CloudPayloadFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    internal CloudPayloadException(
        CloudPayloadFailureReason reason,
        Exception innerException)
        : base(GetStableMessage(reason), innerException)
    {
        Reason = reason;
    }

    internal CloudPayloadFailureReason Reason { get; }

    private static string GetStableMessage(CloudPayloadFailureReason reason) =>
        reason switch
        {
            CloudPayloadFailureReason.InvalidSelection =>
                "The cloud text selection is not bound to the supplied transcript snapshot.",
            CloudPayloadFailureReason.ContextNotAdjacent =>
                "Cloud payload context must be directly adjacent to its selected text.",
            CloudPayloadFailureReason.OverlappingSelection =>
                "Cloud payload source ranges must not overlap.",
            CloudPayloadFailureReason.SensitivityScanIncomplete =>
                "The cloud payload sensitivity scan did not complete.",
            CloudPayloadFailureReason.ConfirmationDigestMismatch =>
                "The cloud payload confirmation does not match the displayed preview.",
            CloudPayloadFailureReason.ConfirmationUnavailable =>
                "The cloud payload preview is not available for confirmation.",
            CloudPayloadFailureReason.PayloadIntegrityMismatch =>
                "The confirmed cloud payload failed its integrity check.",
            _ => "The cloud payload operation failed.",
        };
}

internal sealed class CloudTranscriptTextRange
{
    internal CloudTranscriptTextRange(
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        int utf16Offset,
        int utf16Length)
    {
        ArgumentNullException.ThrowIfNull(utteranceId);
        if (utf16Offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf16Offset),
                "A cloud text-range offset must not be negative.");
        }

        if (utf16Length <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf16Length),
                "A cloud text-range length must be positive.");
        }

        UtteranceId = utteranceId;
        Utf16Offset = utf16Offset;
        Utf16Length = utf16Length;
    }

    internal EntityId<TranscriptUtteranceIdTag> UtteranceId { get; }

    internal int Utf16Offset { get; }

    internal int Utf16Length { get; }
}

internal sealed class CloudTextSelection
{
    internal CloudTextSelection(
        CloudTranscriptTextRange selected,
        CloudTranscriptTextRange? contextBefore = null,
        CloudTranscriptTextRange? contextAfter = null)
    {
        ArgumentNullException.ThrowIfNull(selected);

        Selected = selected;
        ContextBefore = contextBefore;
        ContextAfter = contextAfter;
    }

    internal CloudTranscriptTextRange Selected { get; }

    internal CloudTranscriptTextRange? ContextBefore { get; }

    internal CloudTranscriptTextRange? ContextAfter { get; }
}

internal sealed class CloudPayloadMaskChange
{
    internal const string MaskReplacement = "[MASKED]";

    internal CloudPayloadMaskChange(
        int sourceUtf16Offset,
        int sourceUtf16Length,
        IReadOnlyList<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(reasonCodes);
        if (sourceUtf16Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceUtf16Offset));
        }

        if (sourceUtf16Length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceUtf16Length));
        }

        var copiedReasonCodes = new string[reasonCodes.Count];
        for (int index = 0; index < copiedReasonCodes.Length; index++)
        {
            string reasonCode = reasonCodes[index]
                ?? throw new ArgumentException(
                    "Cloud payload mask reasons must not contain null elements.",
                    nameof(reasonCodes));
            if (reasonCode.Length == 0)
            {
                throw new ArgumentException(
                    "Cloud payload mask reasons must not be empty.",
                    nameof(reasonCodes));
            }

            copiedReasonCodes[index] = reasonCode;
        }

        if (copiedReasonCodes.Length == 0)
        {
            throw new ArgumentException(
                "A cloud payload mask must have at least one reason.",
                nameof(reasonCodes));
        }

        SourceUtf16Offset = sourceUtf16Offset;
        SourceUtf16Length = sourceUtf16Length;
        ReasonCodes = Array.AsReadOnly(copiedReasonCodes);
    }

    internal int SourceUtf16Offset { get; }

    internal int SourceUtf16Length { get; }

    internal string Replacement => MaskReplacement;

    internal IReadOnlyList<string> ReasonCodes { get; }
}

internal sealed class CloudPayloadPreviewSegment
{
    internal CloudPayloadPreviewSegment(
        string evidenceId,
        CloudPayloadSegmentRole role,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<SourceSpanIdTag> sourceSpanId,
        int sourceUtf16Offset,
        int sourceUtf16Length,
        string outboundText,
        IReadOnlyList<CloudPayloadMaskChange> maskChanges)
    {
        ArgumentNullException.ThrowIfNull(evidenceId);
        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        ArgumentNullException.ThrowIfNull(outboundText);
        ArgumentNullException.ThrowIfNull(maskChanges);
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        var copiedChanges = new CloudPayloadMaskChange[maskChanges.Count];
        for (int index = 0; index < copiedChanges.Length; index++)
        {
            copiedChanges[index] = maskChanges[index]
                ?? throw new ArgumentException(
                    "Cloud payload mask changes must not contain null elements.",
                    nameof(maskChanges));
        }

        EvidenceId = evidenceId;
        Role = role;
        UtteranceId = utteranceId;
        SourceSpanId = sourceSpanId;
        SourceUtf16Offset = sourceUtf16Offset;
        SourceUtf16Length = sourceUtf16Length;
        OutboundText = outboundText;
        MaskChanges = Array.AsReadOnly(copiedChanges);
    }

    internal string EvidenceId { get; }

    internal CloudPayloadSegmentRole Role { get; }

    internal EntityId<TranscriptUtteranceIdTag> UtteranceId { get; }

    internal EntityId<SourceSpanIdTag> SourceSpanId { get; }

    internal int SourceUtf16Offset { get; }

    internal int SourceUtf16Length { get; }

    internal string OutboundText { get; }

    internal IReadOnlyList<CloudPayloadMaskChange> MaskChanges { get; }
}

internal sealed class CloudPayloadPreview
{
    internal CloudPayloadPreview(
        EntityId<TranscriptRevisionIdTag> revisionId,
        CloudPayloadPurpose purpose,
        IReadOnlyList<CloudPayloadPreviewSegment> segments,
        ContentDigest payloadDigest,
        ContentDigest confirmationDigest)
    {
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(payloadDigest);
        ArgumentNullException.ThrowIfNull(confirmationDigest);

        var copiedSegments = new CloudPayloadPreviewSegment[segments.Count];
        for (int index = 0; index < copiedSegments.Length; index++)
        {
            copiedSegments[index] = segments[index]
                ?? throw new ArgumentException(
                    "Cloud payload preview segments must not contain null elements.",
                    nameof(segments));
        }

        RevisionId = revisionId;
        Purpose = purpose;
        Segments = Array.AsReadOnly(copiedSegments);
        PayloadDigest = payloadDigest;
        ConfirmationDigest = confirmationDigest;
    }

    internal EntityId<TranscriptRevisionIdTag> RevisionId { get; }

    internal CloudPayloadPurpose Purpose { get; }

    internal string Destination => AzureCloudConfiguration.OpenAiEndpoint.AbsoluteUri;

    internal string Region => AzureCloudConfiguration.Region;

    internal string DeploymentName => AzureCloudConfiguration.DeploymentName;

    internal string DeploymentType => AzureCloudConfiguration.DeploymentType;

    internal string ModelName => AzureCloudConfiguration.ModelName;

    internal string ModelVersion => AzureCloudConfiguration.ModelVersion;

    internal string ProcessingScopeDisclosure =>
        AzureCloudConfiguration.ProcessingScopeDisclosure;

    internal string RetentionDisclosure => AzureCloudConfiguration.RetentionDisclosure;

    internal string SensitivityRuleSetVersion => TextSensitivityScanner.RuleSetVersion;

    internal ContentDigest PayloadDigest { get; }

    internal ContentDigest ConfirmationDigest { get; }

    internal IReadOnlyList<CloudPayloadPreviewSegment> Segments { get; }

    internal bool RequiresExplicitConfirmation => true;
}

internal sealed class CloudPayloadConfirmation
{
    internal CloudPayloadConfirmation(
        ContentDigest confirmationDigest,
        DateTimeOffset confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(confirmationDigest);

        ConfirmationDigest = confirmationDigest;
        ConfirmedAt = confirmedAt;
    }

    internal ContentDigest ConfirmationDigest { get; }

    internal DateTimeOffset ConfirmedAt { get; }
}

internal sealed class ConfirmedCloudPayload
{
    private readonly byte[] _canonicalPayloadUtf8;

    internal ConfirmedCloudPayload(
        CloudPayloadPurpose purpose,
        byte[] canonicalPayloadUtf8,
        ContentDigest payloadDigest,
        ContentDigest confirmationDigest,
        DateTimeOffset confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayloadUtf8);
        ArgumentNullException.ThrowIfNull(payloadDigest);
        ArgumentNullException.ThrowIfNull(confirmationDigest);

        Purpose = purpose;
        _canonicalPayloadUtf8 = canonicalPayloadUtf8.ToArray();
        PayloadDigest = payloadDigest;
        ConfirmationDigest = confirmationDigest;
        ConfirmedAt = confirmedAt;
    }

    internal CloudPayloadPurpose Purpose { get; }

    internal Uri Destination => AzureCloudConfiguration.OpenAiEndpoint;

    internal string Region => AzureCloudConfiguration.Region;

    internal string DeploymentName => AzureCloudConfiguration.DeploymentName;

    internal string DeploymentType => AzureCloudConfiguration.DeploymentType;

    internal string ModelName => AzureCloudConfiguration.ModelName;

    internal string ModelVersion => AzureCloudConfiguration.ModelVersion;

    internal ContentDigest PayloadDigest { get; }

    internal ContentDigest ConfirmationDigest { get; }

    internal DateTimeOffset ConfirmedAt { get; }

    internal byte[] GetPayloadUtf8() => _canonicalPayloadUtf8.ToArray();
}

internal static class CloudPayloadBuilder
{
    internal const string PayloadSchemaVersion = "1.0.0";

    private static readonly ConditionalWeakTable<
        CloudPayloadPreview, PendingPayload> PendingPayloads = new();

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static CloudPayloadPreview BuildPreview(
        TranscriptRevision revision,
        IReadOnlyList<TermEntry> dictionaryEntries,
        CloudPayloadPurpose purpose,
        IReadOnlyList<CloudTextSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);
        ArgumentNullException.ThrowIfNull(selections);
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(
                nameof(purpose),
                purpose,
                "The cloud payload purpose must be defined.");
        }

        if (selections.Count == 0)
        {
            throw Failure(CloudPayloadFailureReason.InvalidSelection);
        }

        TextSensitivityScanResult sensitivity = TextSensitivityScanner.Scan(
            revision,
            dictionaryEntries);
        if (!sensitivity.IsComplete)
        {
            throw Failure(CloudPayloadFailureReason.SensitivityScanIncomplete);
        }

        Dictionary<EntityId<TranscriptUtteranceIdTag>, IndexedUtterance> utterances =
            IndexUtterances(revision);
        List<ResolvedSegment> resolvedSegments = ResolveSelections(
            selections,
            utterances);
        ValidateNoOverlap(resolvedSegments);

        Dictionary<EntityId<TranscriptUtteranceIdTag>, List<SourceMask>> sourceMasks =
            CollectSourceMasks(sensitivity, resolvedSegments);
        var previewSegments = new List<CloudPayloadPreviewSegment>(
            resolvedSegments.Count);
        for (int index = 0; index < resolvedSegments.Count; index++)
        {
            ResolvedSegment segment = resolvedSegments[index];
            IReadOnlyList<CloudPayloadMaskChange> changes = BuildMaskChanges(
                segment,
                sourceMasks[segment.Utterance.Id]);
            string outboundText = ApplyMasks(segment, changes);
            previewSegments.Add(new CloudPayloadPreviewSegment(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"e{index}"),
                segment.Role,
                segment.Utterance.Id,
                segment.Utterance.SourceSpanId,
                segment.Offset,
                segment.Length,
                outboundText,
                changes));
        }

        byte[] payloadUtf8 = WritePayload(purpose, previewSegments);
        ContentDigest payloadDigest = CanonicalJson.ComputeDigest(payloadUtf8);
        ContentDigest confirmationDigest = ComputeConfirmationDigest(
            purpose,
            payloadDigest);
        var preview = new CloudPayloadPreview(
            revision.Id,
            purpose,
            previewSegments,
            payloadDigest,
            confirmationDigest);
        PendingPayloads.Add(preview, new PendingPayload(payloadUtf8));
        return preview;
    }

    internal static ConfirmedCloudPayload Confirm(
        CloudPayloadPreview preview,
        CloudPayloadConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (confirmation.ConfirmationDigest != preview.ConfirmationDigest)
        {
            throw Failure(CloudPayloadFailureReason.ConfirmationDigestMismatch);
        }

        if (!PendingPayloads.TryGetValue(preview, out PendingPayload? pending) ||
            !pending.TryConsume(out byte[]? payloadUtf8))
        {
            throw Failure(CloudPayloadFailureReason.ConfirmationUnavailable);
        }

        _ = PendingPayloads.Remove(preview);
        ContentDigest actualPayloadDigest;
        try
        {
            actualPayloadDigest = CanonicalJson.ComputeDigest(payloadUtf8);
        }
        catch (JsonException exception)
        {
            throw Failure(
                CloudPayloadFailureReason.PayloadIntegrityMismatch,
                exception);
        }

        ContentDigest actualConfirmationDigest = ComputeConfirmationDigest(
            preview.Purpose,
            actualPayloadDigest);
        if (actualPayloadDigest != preview.PayloadDigest ||
            actualConfirmationDigest != preview.ConfirmationDigest)
        {
            throw Failure(CloudPayloadFailureReason.PayloadIntegrityMismatch);
        }

        return new ConfirmedCloudPayload(
            preview.Purpose,
            payloadUtf8,
            preview.PayloadDigest,
            preview.ConfirmationDigest,
            confirmation.ConfirmedAt);
    }

    private static Dictionary<
        EntityId<TranscriptUtteranceIdTag>, IndexedUtterance> IndexUtterances(
        TranscriptRevision revision)
    {
        var utterances = new Dictionary<
            EntityId<TranscriptUtteranceIdTag>, IndexedUtterance>(
            revision.Utterances.Count);
        for (int index = 0; index < revision.Utterances.Count; index++)
        {
            Utterance utterance = revision.Utterances[index];
            utterances.Add(utterance.Id, new IndexedUtterance(index, utterance));
        }

        return utterances;
    }

    private static List<ResolvedSegment> ResolveSelections(
        IReadOnlyList<CloudTextSelection> selections,
        IReadOnlyDictionary<
            EntityId<TranscriptUtteranceIdTag>, IndexedUtterance> utterances)
    {
        var resolved = new List<ResolvedSegment>();
        for (int index = 0; index < selections.Count; index++)
        {
            CloudTextSelection selection = selections[index]
                ?? throw new ArgumentException(
                    "Cloud text selections must not contain null elements.",
                    nameof(selections));
            ResolvedSegment selected = ResolveRange(
                selection.Selected,
                CloudPayloadSegmentRole.Selected,
                utterances);
            ResolvedSegment? before = selection.ContextBefore is null
                ? null
                : ResolveRange(
                    selection.ContextBefore,
                    CloudPayloadSegmentRole.ContextBefore,
                    utterances);
            ResolvedSegment? after = selection.ContextAfter is null
                ? null
                : ResolveRange(
                    selection.ContextAfter,
                    CloudPayloadSegmentRole.ContextAfter,
                    utterances);

            if (before is not null && !IsDirectlyBefore(before, selected) ||
                after is not null && !IsDirectlyBefore(selected, after))
            {
                throw Failure(CloudPayloadFailureReason.ContextNotAdjacent);
            }

            if (before is not null)
            {
                resolved.Add(before);
            }

            resolved.Add(selected);
            if (after is not null)
            {
                resolved.Add(after);
            }
        }

        resolved.Sort(static (left, right) =>
        {
            int comparison = left.UtteranceIndex.CompareTo(right.UtteranceIndex);
            return comparison != 0
                ? comparison
                : left.Offset.CompareTo(right.Offset);
        });
        return resolved;
    }

    private static ResolvedSegment ResolveRange(
        CloudTranscriptTextRange range,
        CloudPayloadSegmentRole role,
        IReadOnlyDictionary<
            EntityId<TranscriptUtteranceIdTag>, IndexedUtterance> utterances)
    {
        if (!utterances.TryGetValue(range.UtteranceId, out IndexedUtterance? indexed))
        {
            throw Failure(CloudPayloadFailureReason.InvalidSelection);
        }

        long end = (long)range.Utf16Offset + range.Utf16Length;
        if (end > indexed.Utterance.Text.Length)
        {
            throw Failure(CloudPayloadFailureReason.InvalidSelection);
        }

        string text = indexed.Utterance.Text.Substring(
            range.Utf16Offset,
            range.Utf16Length);
        try
        {
            _ = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw Failure(CloudPayloadFailureReason.InvalidSelection, exception);
        }

        return new ResolvedSegment(
            indexed.Index,
            indexed.Utterance,
            range.Utf16Offset,
            range.Utf16Length,
            role);
    }

    private static bool IsDirectlyBefore(
        ResolvedSegment before,
        ResolvedSegment after)
    {
        if (before.UtteranceIndex == after.UtteranceIndex)
        {
            return (long)before.Offset + before.Length == after.Offset;
        }

        return before.UtteranceIndex + 1 == after.UtteranceIndex &&
            (long)before.Offset + before.Length == before.Utterance.Text.Length &&
            after.Offset == 0;
    }

    private static void ValidateNoOverlap(IReadOnlyList<ResolvedSegment> segments)
    {
        for (int index = 1; index < segments.Count; index++)
        {
            ResolvedSegment previous = segments[index - 1];
            ResolvedSegment current = segments[index];
            if (previous.UtteranceIndex == current.UtteranceIndex &&
                (long)previous.Offset + previous.Length > current.Offset)
            {
                throw Failure(CloudPayloadFailureReason.OverlappingSelection);
            }
        }
    }

    private static Dictionary<
        EntityId<TranscriptUtteranceIdTag>, List<SourceMask>> CollectSourceMasks(
        TextSensitivityScanResult sensitivity,
        IReadOnlyList<ResolvedSegment> segments)
    {
        var selectedUtterances = segments
            .Select(static segment => segment.Utterance)
            .DistinctBy(static utterance => utterance.Id)
            .ToDictionary(
                static utterance => utterance.Id,
                static _ => new List<SourceMask>());

        foreach (TextSensitivityFinding finding in sensitivity.Findings)
        {
            if (!selectedUtterances.TryGetValue(
                    finding.UtteranceId,
                    out List<SourceMask>? masks))
            {
                continue;
            }

            masks.Add(new SourceMask(
                finding.Utf16Offset,
                finding.Utf16Length,
                finding.ReasonCode));
        }

        foreach (Utterance utterance in segments
            .Select(static segment => segment.Utterance)
            .DistinctBy(static utterance => utterance.Id))
        {
            if (!CloudOutboundTextPolicy.TryFindMasks(
                    utterance.Text,
                    out IReadOnlyList<SourceMask>? outboundMasks))
            {
                throw Failure(CloudPayloadFailureReason.SensitivityScanIncomplete);
            }

            selectedUtterances[utterance.Id].AddRange(outboundMasks);
        }

        return selectedUtterances;
    }

    private static IReadOnlyList<CloudPayloadMaskChange> BuildMaskChanges(
        ResolvedSegment segment,
        IReadOnlyList<SourceMask> sourceMasks)
    {
        long segmentEnd = (long)segment.Offset + segment.Length;
        List<SourceMask> intersected = [];
        foreach (SourceMask mask in sourceMasks)
        {
            long maskEnd = (long)mask.Offset + mask.Length;
            int intersectionStart = Math.Max(segment.Offset, mask.Offset);
            long intersectionEnd = Math.Min(segmentEnd, maskEnd);
            if (intersectionStart < intersectionEnd)
            {
                intersected.Add(new SourceMask(
                    intersectionStart,
                    checked((int)(intersectionEnd - intersectionStart)),
                    mask.ReasonCode));
            }
        }

        if (intersected.Count == 0)
        {
            return Array.Empty<CloudPayloadMaskChange>();
        }

        intersected.Sort(static (left, right) =>
        {
            int comparison = left.Offset.CompareTo(right.Offset);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.Length.CompareTo(left.Length);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.ReasonCode, right.ReasonCode);
        });

        var changes = new List<CloudPayloadMaskChange>();
        int currentStart = intersected[0].Offset;
        int currentEnd = checked(currentStart + intersected[0].Length);
        var reasons = new SortedSet<string>(StringComparer.Ordinal)
        {
            intersected[0].ReasonCode,
        };
        for (int index = 1; index < intersected.Count; index++)
        {
            SourceMask candidate = intersected[index];
            int candidateEnd = checked(candidate.Offset + candidate.Length);
            // Adjacent sensitive ranges form one redaction so no boundary text can escape.
            if (candidate.Offset <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, candidateEnd);
                _ = reasons.Add(candidate.ReasonCode);
                continue;
            }

            changes.Add(new CloudPayloadMaskChange(
                currentStart,
                currentEnd - currentStart,
                reasons.ToArray()));
            currentStart = candidate.Offset;
            currentEnd = candidateEnd;
            reasons = new SortedSet<string>(StringComparer.Ordinal)
            {
                candidate.ReasonCode,
            };
        }

        changes.Add(new CloudPayloadMaskChange(
            currentStart,
            currentEnd - currentStart,
            reasons.ToArray()));
        return changes.AsReadOnly();
    }

    private static string ApplyMasks(
        ResolvedSegment segment,
        IReadOnlyList<CloudPayloadMaskChange> changes)
    {
        if (changes.Count == 0)
        {
            return segment.Utterance.Text.Substring(segment.Offset, segment.Length);
        }

        int segmentEnd = checked(segment.Offset + segment.Length);
        int cursor = segment.Offset;
        var masked = new StringBuilder(segment.Length);
        foreach (CloudPayloadMaskChange change in changes)
        {
            masked.Append(segment.Utterance.Text, cursor, change.SourceUtf16Offset - cursor);
            masked.Append(CloudPayloadMaskChange.MaskReplacement);
            cursor = checked(change.SourceUtf16Offset + change.SourceUtf16Length);
        }

        masked.Append(segment.Utterance.Text, cursor, segmentEnd - cursor);
        return masked.ToString();
    }

    private static byte[] WritePayload(
        CloudPayloadPurpose purpose,
        IReadOnlyList<CloudPayloadPreviewSegment> segments)
    {
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", PayloadSchemaVersion);
            writer.WriteString("purpose", ToWireValue(purpose));
            writer.WritePropertyName("segments");
            writer.WriteStartArray();
            foreach (CloudPayloadPreviewSegment segment in segments)
            {
                writer.WriteStartObject();
                writer.WriteString("evidenceId", segment.EvidenceId);
                writer.WriteString("role", ToWireValue(segment.Role));
                writer.WriteString("text", segment.OutboundText);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static ContentDigest ComputeConfirmationDigest(
        CloudPayloadPurpose purpose,
        ContentDigest payloadDigest)
    {
        byte[] manifest = WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("confirmationSchemaVersion", "1.0.0");
            writer.WriteString(
                "destination",
                AzureCloudConfiguration.OpenAiEndpoint.AbsoluteUri);
            writer.WriteString(
                "deploymentName",
                AzureCloudConfiguration.DeploymentName);
            writer.WriteString(
                "deploymentType",
                AzureCloudConfiguration.DeploymentType);
            writer.WriteString("modelName", AzureCloudConfiguration.ModelName);
            writer.WriteString("modelVersion", AzureCloudConfiguration.ModelVersion);
            writer.WriteString("payloadDigest", payloadDigest.Hex);
            writer.WriteString(
                "processingScopeDisclosure",
                AzureCloudConfiguration.ProcessingScopeDisclosure);
            writer.WriteString("purpose", ToWireValue(purpose));
            writer.WriteString("region", AzureCloudConfiguration.Region);
            writer.WriteString(
                "retentionDisclosure",
                AzureCloudConfiguration.RetentionDisclosure);
            writer.WriteString(
                "sensitivityRuleSetVersion",
                TextSensitivityScanner.RuleSetVersion);
            writer.WriteEndObject();
        });
        return ContentDigest.Compute(manifest);
    }

    private static byte[] WriteCanonical(Action<Utf8JsonWriter> write)
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
            write(writer);
            writer.Flush();
        }

        return CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
    }

    private static string ToWireValue(CloudPayloadPurpose purpose) =>
        purpose switch
        {
            CloudPayloadPurpose.TextSuggestionEvaluation =>
                "text-suggestion-evaluation",
            _ => throw new InvalidOperationException(
                "Unknown cloud payload purpose."),
        };

    private static string ToWireValue(CloudPayloadSegmentRole role) =>
        role switch
        {
            CloudPayloadSegmentRole.ContextBefore => "context-before",
            CloudPayloadSegmentRole.Selected => "selected",
            CloudPayloadSegmentRole.ContextAfter => "context-after",
            _ => throw new InvalidOperationException(
                "Unknown cloud payload segment role."),
        };

    private static CloudPayloadException Failure(
        CloudPayloadFailureReason reason) => new(reason);

    private static CloudPayloadException Failure(
        CloudPayloadFailureReason reason,
        Exception innerException) => new(reason, innerException);

    private sealed record IndexedUtterance(int Index, Utterance Utterance);

    private sealed record ResolvedSegment(
        int UtteranceIndex,
        Utterance Utterance,
        int Offset,
        int Length,
        CloudPayloadSegmentRole Role);

    private sealed class PendingPayload
    {
        private byte[]? _canonicalPayloadUtf8;

        internal PendingPayload(byte[] canonicalPayloadUtf8)
        {
            _canonicalPayloadUtf8 = canonicalPayloadUtf8.ToArray();
        }

        internal bool TryConsume(
            [NotNullWhen(true)] out byte[]? canonicalPayloadUtf8)
        {
            canonicalPayloadUtf8 = Interlocked.Exchange(
                ref _canonicalPayloadUtf8,
                null);
            return canonicalPayloadUtf8 is not null;
        }
    }
}

internal sealed record SourceMask(int Offset, int Length, string ReasonCode);

internal static class CloudOutboundTextPolicy
{
    private const string UrlReason = "cloud-payload.url";
    private const string PathReason = "cloud-payload.path";
    private const string FileNameReason = "cloud-payload.file-name";
    private const string WindowsSidReason = "cloud-payload.windows-sid";
    private const string InternalIdReason = "cloud-payload.internal-id";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Rule[] Rules =
    [
        new(
            CreateRegex(
                """(?<value>(?:(?:[A-Z][A-Z0-9+.-]{1,31})://|mailto:|www\.)[^\s<>"'。、，；！？「」『』（）【】]+)"""),
            UrlReason,
            TrimTrailingPunctuation: true),
        new(
            CreateRegex(
                """["](?<value>(?:[A-Z]:[\\/]|\\\\|/|\.{1,2}/|~/)[^"\r\n]+)["]"""),
            PathReason,
            TrimTrailingPunctuation: false),
        new(
            CreateRegex(
                """['](?<value>(?:[A-Z]:[\\/]|\\\\|/|\.{1,2}/|~/)[^'\r\n]+)[']"""),
            PathReason,
            TrimTrailingPunctuation: false),
        new(
            CreateRegex(
                """(?:^|[^A-Z0-9])(?<value>(?:[A-Z]:[\\/]|\\\\)[^\s<>"|?*\r\n]+)"""),
            PathReason,
            TrimTrailingPunctuation: true),
        new(
            CreateRegex(
                """(?<value>/(?:[\p{L}\p{N}._~-]+/)+[\p{L}\p{N}._~-]+)"""),
            PathReason,
            TrimTrailingPunctuation: true),
        new(
            CreateRegex(
                """(?<value>[\p{L}\p{N}._~-]+(?:[\\/][\p{L}\p{N}._~-]+)+)"""),
            PathReason,
            TrimTrailingPunctuation: true),
        new(
            CreateRegex(
                """(?:^|[^\p{L}\p{N}_@-])(?<value>[\p{L}\p{N}_-]+(?:\.[\p{L}\p{N}_-]+)*\.[A-Z][A-Z0-9]{0,15})"""),
            FileNameReason,
            TrimTrailingPunctuation: false),
        new(
            CreateRegex(
                """(?<value>S-[0-9]+(?:-[0-9]+){2,})"""),
            WindowsSidReason,
            TrimTrailingPunctuation: false),
        new(
            CreateRegex(
                """(?<value>[A-F0-9]{8}-(?:[A-F0-9]{4}-){3}[A-F0-9]{12})"""),
            InternalIdReason,
            TrimTrailingPunctuation: false),
    ];

    internal static bool TryFindMasks(
        string source,
        [NotNullWhen(true)] out IReadOnlyList<SourceMask>? masks)
    {
        if (!NormalizedText.TryCreate(source, out NormalizedText? normalized))
        {
            masks = null;
            return false;
        }

        var findings = new List<SourceMask>();
        try
        {
            foreach (Rule rule in Rules)
            {
                for (Match match = rule.Pattern.Match(normalized.Value);
                    match.Success;
                    match = match.NextMatch())
                {
                    Group value = match.Groups["value"];
                    int length = value.Length;
                    if (rule.TrimTrailingPunctuation)
                    {
                        while (length > 0 && IsTrailingPunctuation(
                            normalized.Value[value.Index + length - 1]))
                        {
                            length--;
                        }
                    }

                    if (length == 0 ||
                        !normalized.TryMapToOriginal(
                            value.Index,
                            length,
                            out int offset,
                            out int originalLength))
                    {
                        masks = null;
                        return false;
                    }

                    findings.Add(new SourceMask(
                        offset,
                        originalLength,
                        rule.ReasonCode));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            masks = null;
            return false;
        }

        masks = findings.AsReadOnly();
        return true;
    }

    private static bool IsTrailingPunctuation(char value) =>
        value is '.' or ',' or ';' or '!' or '?' or ')' or '}'
            or '。' or '、' or '，' or '；' or '！' or '？'
            or '」' or '』' or '）' or '】';

    private static Regex CreateRegex(string pattern) =>
        new(
            pattern,
            RegexOptions.CultureInvariant |
                RegexOptions.IgnoreCase |
                RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1));

    private sealed record Rule(
        Regex Pattern,
        string ReasonCode,
        bool TrimTrailingPunctuation);

    private sealed class NormalizedText
    {
        private NormalizedText(
            string value,
            int[] originalStarts,
            int[] originalEnds)
        {
            Value = value;
            _originalStarts = originalStarts;
            _originalEnds = originalEnds;
        }

        private readonly int[] _originalStarts;
        private readonly int[] _originalEnds;

        internal string Value { get; }

        internal static bool TryCreate(
            string source,
            [NotNullWhen(true)] out NormalizedText? normalizedText)
        {
            try
            {
                _ = StrictUtf8.GetByteCount(source);
                int[] elementStarts = StringInfo.ParseCombiningCharacters(source);
                var value = new StringBuilder(source.Length);
                var originalStarts = new List<int>(source.Length);
                var originalEnds = new List<int>(source.Length);

                for (int index = 0; index < elementStarts.Length; index++)
                {
                    int originalStart = elementStarts[index];
                    int originalEnd = index + 1 < elementStarts.Length
                        ? elementStarts[index + 1]
                        : source.Length;
                    string element = source[originalStart..originalEnd]
                        .Normalize(NormalizationForm.FormKC);
                    value.Append(element);
                    for (int character = 0; character < element.Length; character++)
                    {
                        originalStarts.Add(originalStart);
                        originalEnds.Add(originalEnd);
                    }
                }

                normalizedText = new NormalizedText(
                    value.ToString(),
                    originalStarts.ToArray(),
                    originalEnds.ToArray());
                return true;
            }
            catch (ArgumentException)
            {
                normalizedText = null;
                return false;
            }
        }

        internal bool TryMapToOriginal(
            int normalizedOffset,
            int normalizedLength,
            out int originalOffset,
            out int originalLength)
        {
            long normalizedEnd = (long)normalizedOffset + normalizedLength;
            if (normalizedOffset < 0 ||
                normalizedLength <= 0 ||
                normalizedEnd > _originalStarts.Length)
            {
                originalOffset = 0;
                originalLength = 0;
                return false;
            }

            originalOffset = _originalStarts[normalizedOffset];
            int originalEnd = _originalEnds[(int)normalizedEnd - 1];
            originalLength = originalEnd - originalOffset;
            return originalLength > 0;
        }
    }
}