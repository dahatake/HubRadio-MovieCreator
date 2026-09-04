using System.Globalization;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;

namespace HubRadio.App.Compositions;

internal sealed class ValidatedAutoDecisionEvidence
{
    internal ValidatedAutoDecisionEvidence(CompositionEvidenceEnvelope envelope)
    {
        Envelope = envelope;
    }

    internal CompositionEvidenceEnvelope Envelope { get; }
}

internal static class AutoDecisionEvidenceCodec
{
    internal static ValidatedAutoDecisionEvidence ValidateForCheckpoint(
        CompositionVersion child,
        CompositionVersion parent)
    {
        ValidateLoaded(child, parent);
        return new ValidatedAutoDecisionEvidence(
            child.Evidence
                ?? throw new FormatException("Auto-decision evidence is missing."));
    }

    internal static void ValidateLoaded(
        CompositionVersion child,
        CompositionVersion parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        CompositionEvidenceEnvelope envelope = child.Evidence
            ?? throw new FormatException("Auto-decision evidence is missing.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(envelope.CanonicalJson);
            JsonElement root = document.RootElement;
            RequireExactObject(
                root,
                "evidence",
                [
                    "schemaVersion",
                    "input",
                    "inputDigest",
                    "decisionSetDigest",
                    "child",
                    "decisions",
                    "decisionHistory",
                    "timelineMap",
                ]);
            RequireString(
                root,
                "schemaVersion",
                AutoDecisionOrchestrator.EvidenceSchemaVersion,
                "evidence");
            _ = ReadDigest(root, "inputDigest", "evidence");
            _ = ReadDigest(root, "decisionSetDigest", "evidence");
            RequireExactObject(
                Property(root, "child", "evidence"),
                "evidence.child",
                ["id", "contentDigest"]);

            JsonElement input = Property(root, "input", "evidence");
            AutoDecisionRequest request = ReadRequest(input, parent);
            AutoDecisionPreparation rebuilt = AutoDecisionOrchestrator.Prepare(request);
            if (!string.Equals(
                    CompositionSnapshotJson.WriteVersion(rebuilt.Child),
                    CompositionSnapshotJson.WriteVersion(child),
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "Stored auto-decision evidence does not reproduce its child version.");
            }
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException
                or ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            throw new FormatException("Auto-decision evidence is semantically invalid.", exception);
        }
    }

    private static AutoDecisionRequest ReadRequest(
        JsonElement input,
        CompositionVersion parent)
    {
        RequireExactObject(
            input,
            "input",
            [
                "workingCopy",
                "parent",
                "actorSid",
                "createdAt",
                "changeReason",
                "settingsProfile",
                "settingsSnapshot",
                "transcript",
                "sourceSpans",
                "modelRuns",
                "candidates",
                "priorDecisionHistory",
            ]);

        JsonElement parentValue = Property(input, "parent", "input");
        RequireExactObject(parentValue, "input.parent", ["id", "contentDigest"]);
        if (ReadId<CompositionVersionIdTag>(parentValue, "id", "input.parent") != parent.Id
            || ReadDigest(parentValue, "contentDigest", "input.parent") != parent.ContentDigest)
        {
            throw new FormatException(
                "Auto-decision evidence is not bound to the actual stored parent.");
        }

        DefaultSettingsProfile profile = DefaultSettingsProfile.ParseCanonical(
            Property(input, "settingsProfile", "input").GetRawText());
        DefaultSettingsSnapshot settings = DefaultSettingsSnapshot.ParseCanonical(
            Property(input, "settingsSnapshot", "input").GetRawText(),
            profile);
        TranscriptRevision transcript = ReadTranscript(
            Property(input, "transcript", "input"));
        SourceSpan[] sourceSpans = ReadArray(
            Property(input, "sourceSpans", "input"),
            "input.sourceSpans",
            ReadSourceSpan);
        ModelRun[] modelRuns = ReadArray(
            Property(input, "modelRuns", "input"),
            "input.modelRuns",
            ReadModelRun);
        SuggestionCandidate[] candidates = ReadArray(
            Property(input, "candidates", "input"),
            "input.candidates",
            ReadCandidate);
        SuggestionDecisionRecord[] history = ReadArray(
            Property(input, "priorDecisionHistory", "input"),
            "input.priorDecisionHistory",
            ReadDecisionRecord);

        JsonElement workingCopyValue = Property(input, "workingCopy", "input");
        RequireExactObject(
            workingCopyValue,
            "input.workingCopy",
            ["id", "projectId", "baseVersionId", "revision", "createdAt", "updatedAt"]);
        EntityId<CompositionVersionIdTag>? baseVersionId = ReadNullableId<CompositionVersionIdTag>(
            workingCopyValue,
            "baseVersionId",
            "input.workingCopy");
        var workingCopy = new WorkingCopy(
            ReadId<WorkingCopyIdTag>(workingCopyValue, "id", "input.workingCopy"),
            ReadId<ProjectIdTag>(workingCopyValue, "projectId", "input.workingCopy"),
            baseVersionId,
            ReadTimestamp(workingCopyValue, "createdAt", "input.workingCopy"),
            ReadTimestamp(workingCopyValue, "updatedAt", "input.workingCopy"),
            parent.Elements);

        long revision = ReadInt64(workingCopyValue, "revision", "input.workingCopy");
        if (revision is < 0 or > int.MaxValue)
        {
            throw new FormatException("The auto-decision working-copy revision is invalid.");
        }

        return new AutoDecisionRequest(
            parent,
            workingCopy,
            (int)revision,
            profile,
            settings,
            transcript,
            sourceSpans,
            modelRuns,
            candidates,
            history,
            ReadString(input, "actorSid", "input"),
            ReadTimestamp(input, "createdAt", "input"),
            ReadString(input, "changeReason", "input"));
    }

    private static TranscriptRevision ReadTranscript(JsonElement value)
    {
        RequireExactObject(
            value,
            "input.transcript",
            ["id", "parentRevisionId", "changeSource", "createdAt", "utterances"]);
        TranscriptChangeSource source = ReadString(
            value,
            "changeSource",
            "input.transcript") switch
        {
            "machine" => TranscriptChangeSource.Machine,
            "human" => TranscriptChangeSource.Human,
            _ => throw new FormatException("The transcript change source is invalid."),
        };
        Utterance[] utterances = ReadArray(
            Property(value, "utterances", "input.transcript"),
            "input.transcript.utterances",
            ReadUtterance);
        var transcript = new TranscriptRevision(
            ReadId<TranscriptRevisionIdTag>(value, "id", "input.transcript"),
            ReadNullableId<TranscriptRevisionIdTag>(
                value,
                "parentRevisionId",
                "input.transcript"),
            source,
            ReadTimestamp(value, "createdAt", "input.transcript"),
            utterances);
        if (!string.Equals(
                TranscriptSnapshotJson.WriteRevision(transcript),
                value.GetRawText(),
                StringComparison.Ordinal))
        {
            throw new FormatException("The transcript is not in canonical domain form.");
        }

        return transcript;
    }

    private static Utterance ReadUtterance(JsonElement value)
    {
        RequireExactObject(
            value,
            "utterance",
            [
                "id",
                "sourceSpanId",
                "speakerId",
                "speakerLabel",
                "text",
                "range",
                "recognitionConfidence",
                "sentences",
            ]);
        return new Utterance(
            ReadId<TranscriptUtteranceIdTag>(value, "id", "utterance"),
            ReadId<SourceSpanIdTag>(value, "sourceSpanId", "utterance"),
            ReadId<TranscriptSpeakerIdTag>(value, "speakerId", "utterance"),
            ReadString(value, "speakerLabel", "utterance"),
            ReadString(value, "text", "utterance"),
            ReadTimeRange(Property(value, "range", "utterance"), "utterance.range"),
            ReadNullableDouble(value, "recognitionConfidence", "utterance"),
            ReadArray(
                Property(value, "sentences", "utterance"),
                "utterance.sentences",
                ReadSentence));
    }

    private static Sentence ReadSentence(JsonElement value)
    {
        RequireExactObject(value, "sentence", ["id", "text", "tokens"]);
        return new Sentence(
            ReadId<TranscriptSentenceIdTag>(value, "id", "sentence"),
            ReadString(value, "text", "sentence"),
            ReadArray(Property(value, "tokens", "sentence"), "sentence.tokens", ReadToken));
    }

    private static Token ReadToken(JsonElement value)
    {
        RequireExactObject(
            value,
            "token",
            ["id", "text", "range", "recognitionConfidence", "manualTimingAdjustmentRequired"]);
        JsonElement rangeValue = Property(value, "range", "token");
        TimeRange? range = rangeValue.ValueKind == JsonValueKind.Null
            ? null
            : ReadTimeRange(rangeValue, "token.range");
        var token = new Token(
            ReadId<TranscriptTokenIdTag>(value, "id", "token"),
            ReadString(value, "text", "token"),
            range,
            ReadNullableDouble(value, "recognitionConfidence", "token"));
        if (ReadBoolean(value, "manualTimingAdjustmentRequired", "token")
            != token.ManualTimingAdjustmentRequired)
        {
            throw new FormatException("The token timing flag is invalid.");
        }

        return token;
    }

    private static SourceSpan ReadSourceSpan(JsonElement value)
    {
        RequireExactObject(
            value,
            "sourceSpan",
            [
                "id",
                "assetId",
                "streamId",
                "range",
                "sourceDigest",
                "transcriptRevisionId",
                "transcriptTokenIds",
            ]);
        return new SourceSpan(
            ReadId<SourceSpanIdTag>(value, "id", "sourceSpan"),
            ReadId<AssetIdTag>(value, "assetId", "sourceSpan"),
            ReadString(value, "streamId", "sourceSpan"),
            ReadTimeRange(Property(value, "range", "sourceSpan"), "sourceSpan.range"),
            ReadDigest(value, "sourceDigest", "sourceSpan"),
            ReadNullableId<TranscriptRevisionIdTag>(
                value,
                "transcriptRevisionId",
                "sourceSpan"),
            ReadIdArray<TranscriptTokenIdTag>(
                Property(value, "transcriptTokenIds", "sourceSpan"),
                "sourceSpan.transcriptTokenIds"));
    }

    private static ModelRun ReadModelRun(JsonElement value)
    {
        RequireExactObject(
            value,
            "modelRun",
            [
                "id",
                "provider",
                "modelIdentifier",
                "modelVersion",
                "modelHash",
                "modelHashAvailability",
                "configurationDigest",
                "inputDigest",
                "outputDigest",
                "outcome",
                "errorCode",
                "executionLocation",
                "destination",
                "region",
                "sentDataClassification",
                "retentionPolicy",
                "startedAt",
                "completedAt",
            ]);
        return new ModelRun(
            ReadId<ModelRunIdTag>(value, "id", "modelRun"),
            ReadString(value, "provider", "modelRun"),
            ReadString(value, "modelIdentifier", "modelRun"),
            ReadString(value, "modelVersion", "modelRun"),
            ReadNullableDigest(value, "modelHash", "modelRun"),
            ReadEnum<ModelHashAvailability>(value, "modelHashAvailability", "modelRun"),
            ReadDigest(value, "configurationDigest", "modelRun"),
            ReadDigest(value, "inputDigest", "modelRun"),
            ReadNullableDigest(value, "outputDigest", "modelRun"),
            ReadEnum<ModelRunOutcome>(value, "outcome", "modelRun"),
            ReadNullableString(value, "errorCode", "modelRun"),
            ReadEnum<ModelExecutionLocation>(value, "executionLocation", "modelRun"),
            ReadNullableString(value, "destination", "modelRun"),
            ReadNullableString(value, "region", "modelRun"),
            ReadNullableString(value, "sentDataClassification", "modelRun"),
            ReadNullableString(value, "retentionPolicy", "modelRun"),
            ReadTimestamp(value, "startedAt", "modelRun"),
            ReadTimestamp(value, "completedAt", "modelRun"));
    }

    private static SuggestionCandidate ReadCandidate(JsonElement value)
    {
        RequireExactObject(
            value,
            "candidate",
            [
                "id",
                "sourceDigest",
                "producer",
                "parentSuggestionId",
                "replacementSuggestionId",
                "suggestion",
                "producerSuggestion",
                "payload",
            ]);
        Suggestion suggestion = ReadSuggestion(Property(value, "suggestion", "candidate"));
        Suggestion producerSuggestion = ReadSuggestion(
            Property(value, "producerSuggestion", "candidate"));
        EntityId<SuggestionIdTag>? parentSuggestionId = ReadNullableId<SuggestionIdTag>(
            value,
            "parentSuggestionId",
            "candidate");
        EntityId<SuggestionIdTag>? replacementSuggestionId = ReadNullableId<SuggestionIdTag>(
            value,
            "replacementSuggestionId",
            "candidate");
        SuggestionProducerKind producer = ReadEnum<SuggestionProducerKind>(
            value,
            "producer",
            "candidate");
        JsonElement payload = Property(value, "payload", "candidate");
        SuggestionCandidate candidate = producer switch
        {
            SuggestionProducerKind.EditorialPhrase => new EditorialPhraseSuggestionCandidate(
                ReadEditorialPayload(payload, producerSuggestion),
                suggestion,
                parentSuggestionId,
                replacementSuggestionId),
            SuggestionProducerKind.Repetition => new RepetitionSuggestionCandidate(
                ReadRepetitionPayload(payload),
                producerSuggestion,
                suggestion,
                parentSuggestionId,
                replacementSuggestionId),
            SuggestionProducerKind.Silence => new SilenceSuggestionCandidate(
                ReadSilencePayload(payload, producerSuggestion),
                suggestion,
                parentSuggestionId,
                replacementSuggestionId),
            _ => throw new FormatException("The candidate is not a closed auto-decision rule."),
        };
        if (candidate.Id != ReadId<SuggestionIdTag>(value, "id", "candidate")
            || candidate.SourceDigest != ReadDigest(value, "sourceDigest", "candidate")
            || candidate.Producer != producer
            || !SuggestionsEqual(candidate.ProducerSuggestion, producerSuggestion))
        {
            throw new FormatException("The candidate binding is invalid.");
        }

        return candidate;
    }

    private static EditorialPhraseCandidate ReadEditorialPayload(
        JsonElement value,
        Suggestion producerSuggestion)
    {
        RequireExactObject(
            value,
            "editorialPayload",
            [
                "detection",
                "sourceSpan",
                "sourceRevisionId",
                "sourceRevisionDigest",
                "dictionaryVersion",
                "matchedPhrase",
                "normalizedSentenceStart",
                "normalizedMatchLength",
                "usesUtteranceTimingFallback",
                "usesPartialTokenCoverage",
            ]);
        long start = ReadInt64(value, "normalizedSentenceStart", "editorialPayload");
        long length = ReadInt64(value, "normalizedMatchLength", "editorialPayload");
        if (start is < int.MinValue or > int.MaxValue
            || length is < int.MinValue or > int.MaxValue)
        {
            throw new FormatException("The editorial payload range is invalid.");
        }

        return new EditorialPhraseCandidate(
            ReadDetection(Property(value, "detection", "editorialPayload")),
            producerSuggestion,
            ReadSourceSpan(Property(value, "sourceSpan", "editorialPayload")),
            ReadId<TranscriptRevisionIdTag>(value, "sourceRevisionId", "editorialPayload"),
            ReadDigest(value, "sourceRevisionDigest", "editorialPayload"),
            ReadString(value, "dictionaryVersion", "editorialPayload"),
            ReadString(value, "matchedPhrase", "editorialPayload"),
            (int)start,
            (int)length,
            ReadBoolean(value, "usesUtteranceTimingFallback", "editorialPayload"),
            ReadBoolean(value, "usesPartialTokenCoverage", "editorialPayload"));
    }

    private static RepetitionSuggestionPayload ReadRepetitionPayload(JsonElement value)
    {
        RequireExactObject(
            value,
            "repetitionPayload",
            [
                "candidateKey",
                "modelRunId",
                "sourceRevisionId",
                "sourceRevisionDigest",
                "sourceSpanId",
                "evidenceTokenIds",
                "ruleVersion",
                "kind",
                "status",
                "reason",
                "confidence",
                "automaticDeletionAllowed",
                "humanReviewRequired",
                "features",
            ]);
        JsonElement featuresValue = Property(value, "features", "repetitionPayload");
        RequireExactObject(
            featuresValue,
            "repetitionFeatures",
            [
                "rule",
                "comparisonSourceSpanId",
                "comparisonTokenIds",
                "matchingTokenCount",
                "exactNfkcMatch",
                "structurallyAdjacent",
                "sameSpeakerRequired",
                "candidateIsLaterOccurrence",
                "retentionChoiceRequired",
            ]);
        long count = ReadInt64(featuresValue, "matchingTokenCount", "repetitionFeatures");
        if (count is < int.MinValue or > int.MaxValue)
        {
            throw new FormatException("The repetition token count is invalid.");
        }

        var features = new RepetitionReasonFeatures(
            ReadString(featuresValue, "rule", "repetitionFeatures"),
            ReadId<SourceSpanIdTag>(
                featuresValue,
                "comparisonSourceSpanId",
                "repetitionFeatures"),
            ReadIdArray<TranscriptTokenIdTag>(
                Property(featuresValue, "comparisonTokenIds", "repetitionFeatures"),
                "repetitionFeatures.comparisonTokenIds"),
            (int)count,
            ReadBoolean(featuresValue, "exactNfkcMatch", "repetitionFeatures"),
            ReadBoolean(featuresValue, "structurallyAdjacent", "repetitionFeatures"),
            ReadBoolean(featuresValue, "sameSpeakerRequired", "repetitionFeatures"),
            ReadBoolean(featuresValue, "candidateIsLaterOccurrence", "repetitionFeatures"),
            ReadBoolean(featuresValue, "retentionChoiceRequired", "repetitionFeatures"));
        return new RepetitionSuggestionPayload(
            ReadDigest(value, "candidateKey", "repetitionPayload"),
            ReadId<ModelRunIdTag>(value, "modelRunId", "repetitionPayload"),
            ReadId<TranscriptRevisionIdTag>(value, "sourceRevisionId", "repetitionPayload"),
            ReadDigest(value, "sourceRevisionDigest", "repetitionPayload"),
            ReadId<SourceSpanIdTag>(value, "sourceSpanId", "repetitionPayload"),
            ReadIdArray<TranscriptTokenIdTag>(
                Property(value, "evidenceTokenIds", "repetitionPayload"),
                "repetitionPayload.evidenceTokenIds"),
            ReadString(value, "ruleVersion", "repetitionPayload"),
            ReadEnum<SuggestionKind>(value, "kind", "repetitionPayload"),
            ReadEnum<SuggestionStatus>(value, "status", "repetitionPayload"),
            ReadString(value, "reason", "repetitionPayload"),
            ReadNullableDouble(value, "confidence", "repetitionPayload"),
            ReadBoolean(value, "automaticDeletionAllowed", "repetitionPayload"),
            ReadBoolean(value, "humanReviewRequired", "repetitionPayload"),
            features);
    }

    private static SilenceCandidate ReadSilencePayload(
        JsonElement value,
        Suggestion producerSuggestion)
    {
        RequireExactObject(
            value,
            "silencePayload",
            [
                "analysisTimeline",
                "sourceSpan",
                "detection",
                "thresholdVersion",
                "analysisStartSample",
                "analysisEndSample",
            ]);
        return new SilenceCandidate(
            AnalysisAudioTimeline.ParseCanonical(
                Property(value, "analysisTimeline", "silencePayload").GetRawText()),
            ReadSourceSpan(Property(value, "sourceSpan", "silencePayload")),
            ReadDetection(Property(value, "detection", "silencePayload")),
            producerSuggestion,
            ReadString(value, "thresholdVersion", "silencePayload"),
            ReadInt64(value, "analysisStartSample", "silencePayload"),
            ReadInt64(value, "analysisEndSample", "silencePayload"));
    }

    private static Detection ReadDetection(JsonElement value)
    {
        RequireExactObject(
            value,
            "detection",
            ["id", "modelRunId", "kind", "origin", "description", "confidence"]);
        return new Detection(
            ReadId<DetectionIdTag>(value, "id", "detection"),
            ReadId<ModelRunIdTag>(value, "modelRunId", "detection"),
            ReadEnum<DetectionKind>(value, "kind", "detection"),
            ReadOrigin(Property(value, "origin", "detection")),
            ReadString(value, "description", "detection"),
            ReadNullableDouble(value, "confidence", "detection"));
    }

    private static Suggestion ReadSuggestion(JsonElement value)
    {
        RequireExactObject(
            value,
            "suggestion",
            ["id", "modelRunId", "kind", "status", "origin", "reason", "confidence", "missingContext", "warning"]);
        return new Suggestion(
            ReadId<SuggestionIdTag>(value, "id", "suggestion"),
            ReadId<ModelRunIdTag>(value, "modelRunId", "suggestion"),
            ReadEnum<SuggestionKind>(value, "kind", "suggestion"),
            ReadEnum<SuggestionStatus>(value, "status", "suggestion"),
            ReadOrigin(Property(value, "origin", "suggestion")),
            ReadString(value, "reason", "suggestion"),
            ReadNullableDouble(value, "confidence", "suggestion"),
            ReadNullableString(value, "missingContext", "suggestion"),
            ReadNullableString(value, "warning", "suggestion"));
    }

    private static SuggestionDecisionRecord ReadDecisionRecord(JsonElement value)
    {
        RequireExactObject(
            value,
            "decisionRecord",
            [
                "id",
                "candidateId",
                "modelRunId",
                "producer",
                "suggestionKind",
                "decision",
                "statusAfterDecision",
                "origin",
                "sourceDigest",
                "actorSid",
                "decidedAt",
                "reason",
                "replacementSuggestionId",
                "adoptedReference",
                "resultingTranscriptRevisionId",
            ]);
        JsonElement adoptedValue = Property(value, "adoptedReference", "decisionRecord");
        SuggestionReference? adopted = null;
        if (adoptedValue.ValueKind != JsonValueKind.Null)
        {
            RequireExactObject(adoptedValue, "adoptedReference", ["suggestionId", "origin"]);
            adopted = SuggestionReference.FromPersistedReference(
                ReadId<SuggestionIdTag>(adoptedValue, "suggestionId", "adoptedReference"),
                ReadOrigin(Property(adoptedValue, "origin", "adoptedReference")));
        }

        return new SuggestionDecisionRecord(
            ReadId<SuggestionDecisionIdTag>(value, "id", "decisionRecord"),
            ReadId<SuggestionIdTag>(value, "candidateId", "decisionRecord"),
            ReadId<ModelRunIdTag>(value, "modelRunId", "decisionRecord"),
            ReadEnum<SuggestionProducerKind>(value, "producer", "decisionRecord"),
            ReadEnum<SuggestionKind>(value, "suggestionKind", "decisionRecord"),
            ReadEnum<SuggestionDecisionKind>(value, "decision", "decisionRecord"),
            ReadEnum<SuggestionStatus>(value, "statusAfterDecision", "decisionRecord"),
            ReadOrigin(Property(value, "origin", "decisionRecord")),
            ReadDigest(value, "sourceDigest", "decisionRecord"),
            ReadString(value, "actorSid", "decisionRecord"),
            ReadTimestamp(value, "decidedAt", "decisionRecord"),
            ReadString(value, "reason", "decisionRecord"),
            ReadNullableId<SuggestionIdTag>(
                value,
                "replacementSuggestionId",
                "decisionRecord"),
            adopted,
            ReadNullableId<TranscriptRevisionIdTag>(
                value,
                "resultingTranscriptRevisionId",
                "decisionRecord"));
    }

    private static OriginRef ReadOrigin(JsonElement value)
    {
        RequireExactObject(
            value,
            "origin",
            ["kind", "sourceSpanId", "assetId", "editorialRecordId"]);
        string kind = ReadString(value, "kind", "origin");
        EntityId<SourceSpanIdTag>? sourceSpanId = ReadNullableId<SourceSpanIdTag>(
            value,
            "sourceSpanId",
            "origin");
        EntityId<AssetIdTag>? assetId = ReadNullableId<AssetIdTag>(
            value,
            "assetId",
            "origin");
        EntityId<EditorialRecordIdTag>? editorialRecordId = ReadNullableId<EditorialRecordIdTag>(
            value,
            "editorialRecordId",
            "origin");
        return kind switch
        {
            "SourceSpan" when sourceSpanId is not null
                && assetId is null
                && editorialRecordId is null => OriginRef.FromSourceSpan(sourceSpanId),
            "Asset" when sourceSpanId is null
                && assetId is not null
                && editorialRecordId is null => OriginRef.FromAsset(assetId),
            "EditorialRecord" when sourceSpanId is null
                && assetId is null
                && editorialRecordId is not null => OriginRef.FromEditorialRecord(editorialRecordId),
            _ => throw new FormatException("The origin union is invalid."),
        };
    }

    private static TimeRange ReadTimeRange(JsonElement value, string context)
    {
        RequireExactObject(value, context, ["start", "duration"]);
        return new TimeRange(
            ReadRationalTime(Property(value, "start", context), $"{context}.start"),
            ReadRationalTime(Property(value, "duration", context), $"{context}.duration"));
    }

    private static RationalTime ReadRationalTime(JsonElement value, string context)
    {
        RequireExactObject(value, context, ["value", "rate"]);
        long numerator = ReadInt64(value, "value", context);
        long rate = ReadInt64(value, "rate", context);
        var result = new RationalTime(numerator, rate);
        if (result.Value != numerator || result.Rate != rate)
        {
            throw new FormatException("A rational time must be reduced.");
        }

        return result;
    }

    private static bool SuggestionsEqual(Suggestion left, Suggestion right) =>
        left.Id == right.Id
        && left.ModelRunId == right.ModelRunId
        && left.Kind == right.Kind
        && left.Status == right.Status
        && left.Origin == right.Origin
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && left.Confidence == right.Confidence
        && string.Equals(left.MissingContext, right.MissingContext, StringComparison.Ordinal)
        && string.Equals(left.Warning, right.Warning, StringComparison.Ordinal);

    private static T[] ReadArray<T>(
        JsonElement value,
        string context,
        Func<JsonElement, T> read)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"The {context} value must be an array.");
        }

        return value.EnumerateArray().Select(read).ToArray();
    }

    private static EntityId<TTag>[] ReadIdArray<TTag>(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"The {context} value must be an array.");
        }

        return value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string text)
            {
                throw new FormatException($"The {context} item is invalid.");
            }

            return ParseId<TTag>(text, context);
        }).ToArray();
    }

    private static T ReadEnum<T>(JsonElement parent, string name, string context)
        where T : struct, Enum
    {
        string text = ReadString(parent, name, context);
        if (!Enum.TryParse(text, ignoreCase: false, out T value)
            || !Enum.IsDefined(value)
            || !string.Equals(text, value.ToString(), StringComparison.Ordinal))
        {
            throw new FormatException($"The {context}.{name} enum is invalid.");
        }

        return value;
    }

    private static EntityId<TTag> ReadId<TTag>(
        JsonElement parent,
        string name,
        string context) => ParseId<TTag>(ReadString(parent, name, context), context);

    private static EntityId<TTag> ParseId<TTag>(string text, string context)
    {
        if (!EntityId<TTag>.TryParse(text, out EntityId<TTag>? id)
            || !string.Equals(text, id.ToString(), StringComparison.Ordinal))
        {
            throw new FormatException($"The {context} entity ID is invalid.");
        }

        return id;
    }

    private static EntityId<TTag>? ReadNullableId<TTag>(
        JsonElement parent,
        string name,
        string context)
    {
        JsonElement value = Property(parent, name, context);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.String && value.GetString() is string text
                ? ParseId<TTag>(text, context)
                : throw new FormatException($"The {context}.{name} entity ID is invalid.");
    }

    private static ContentDigest ReadDigest(JsonElement parent, string name, string context)
    {
        string text = ReadString(parent, name, context);
        if (!ContentDigest.TryParse(text, out ContentDigest? digest)
            || !string.Equals(text, digest.Hex, StringComparison.Ordinal))
        {
            throw new FormatException($"The {context}.{name} digest is invalid.");
        }

        return digest;
    }

    private static ContentDigest? ReadNullableDigest(
        JsonElement parent,
        string name,
        string context)
    {
        JsonElement value = Property(parent, name, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadDigest(parent, name, context);
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement parent,
        string name,
        string context)
    {
        string text = ReadString(parent, name, context);
        if (!DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset timestamp)
            || timestamp.Offset != TimeSpan.Zero
            || !string.Equals(
                text,
                timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new FormatException($"The {context}.{name} timestamp is invalid.");
        }

        return timestamp;
    }

    private static long ReadInt64(JsonElement parent, string name, string context)
    {
        JsonElement value = Property(parent, name, context);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out decimal number)
            || number != decimal.Truncate(number)
            || number is < long.MinValue or > long.MaxValue)
        {
            throw new FormatException($"The {context}.{name} Int64 is invalid.");
        }

        return decimal.ToInt64(number);
    }

    private static bool ReadBoolean(JsonElement parent, string name, string context)
    {
        JsonElement value = Property(parent, name, context);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException($"The {context}.{name} boolean is invalid.");
        }

        return value.GetBoolean();
    }

    private static double? ReadNullableDouble(
        JsonElement parent,
        string name,
        string context)
    {
        JsonElement value = Property(parent, name, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"The {context}.{name} number is invalid.");
        }

        return value.GetDouble();
    }

    private static string ReadString(JsonElement parent, string name, string context)
    {
        JsonElement value = Property(parent, name, context);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw new FormatException($"The {context}.{name} string is invalid.");
        }

        return text;
    }

    private static string? ReadNullableString(
        JsonElement parent,
        string name,
        string context)
    {
        JsonElement value = Property(parent, name, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadString(parent, name, context);
    }

    private static void RequireString(
        JsonElement parent,
        string name,
        string expected,
        string context)
    {
        if (!string.Equals(ReadString(parent, name, context), expected, StringComparison.Ordinal))
        {
            throw new FormatException($"The {context}.{name} value is unsupported.");
        }
    }

    private static JsonElement Property(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            throw new FormatException($"The {context}.{name} property is missing.");
        }

        return value;
    }

    private static void RequireExactObject(
        JsonElement value,
        string context,
        IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"The {context} value must be an object.");
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal)
                || !found.Add(property.Name))
            {
                throw new FormatException($"The {context} object is not closed.");
            }
        }

        if (found.Count != expected.Count)
        {
            throw new FormatException($"The {context} object is incomplete.");
        }
    }
}
