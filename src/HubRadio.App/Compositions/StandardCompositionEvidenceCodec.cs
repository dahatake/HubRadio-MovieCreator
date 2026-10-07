using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.App.Analysis;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.App.Speakers;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Compositions;

internal sealed class ValidatedStandardCompositionEvidence
{
    internal ValidatedStandardCompositionEvidence(
        CompositionEvidenceEnvelope envelope,
        StandardCompositionPreparation preparation)
    {
        Envelope = envelope;
        Preparation = preparation;
    }

    internal CompositionEvidenceEnvelope Envelope { get; }

    internal StandardCompositionPreparation Preparation { get; }
}

internal static class StandardCompositionEvidenceCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions =
        CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new CanonicalInt32Converter());
        options.Converters.Add(new CanonicalInt64Converter());
        return options;
    }

    internal static string WriteEvidence(
        StandardCompositionOrchestrationRequest request,
        LongFormCompositionRequest replayLongFormRequest,
        StandardCompositionPlan plan,
        ContentDigest applicationDigest,
        CompositionVersion childWithoutEvidence,
        TimelineMap parentTimelineMap,
        TimelineMap childTimelineMap)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replayLongFormRequest);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(applicationDigest);
        ArgumentNullException.ThrowIfNull(childWithoutEvidence);
        ArgumentNullException.ThrowIfNull(parentTimelineMap);
        ArgumentNullException.ThrowIfNull(childTimelineMap);

        var document = new EvidenceDocumentDto(
            StandardCompositionOrchestrator.EvidenceSchemaVersion,
            new InputDto(
                request.Parent.Id.ToString(),
                request.Parent.ContentDigest.Hex,
                WorkingCopyDto.From(
                    request.WorkingCopy,
                    request.ExpectedWorkingCopyRevision),
                ProjectStoreStateDto.From(request.ProjectStoreState),
                request.SettingsProfile.CanonicalJson,
                request.Settings.CanonicalJson,
                CanonicalEmbeddedJson(request.MeasuredLandscapeProfileJson.Span),
                CanonicalEmbeddedJson(request.MeasuredVerticalProfileJson.Span),
                LongFormRequestDto.From(replayLongFormRequest),
                request.ParentTimelineMapId.ToString(),
                new SourceGeometryDto(
                    request.SourceDisplayGeometry.Width,
                    request.SourceDisplayGeometry.Height),
                RationalDto.From(request.SourceFrameRate),
                request.ToolDigest.Hex,
                request.ModelDigest.Hex,
                MusicPlanningDto.From(request.MusicPlanning),
                request.TopicMetadataRevisions
                    .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal)
                    .Select(TopicMetadataRevisionDto.From)
                    .ToArray(),
                request.Candidates
                    .OrderBy(static item => item.Payload.SourceSpan.AssetId.ToString(),
                        StringComparer.Ordinal)
                    .ThenBy(static item => item.Payload.SourceSpan.StreamId,
                        StringComparer.Ordinal)
                    .ThenBy(static item => item.Payload.SourceSpan.Range.Start)
                    .ThenBy(static item => item.Payload.SourceSpan.Range.End)
                    .ThenBy(static item => item.Id.ToString(), StringComparer.Ordinal)
                    .Select(EmphasisCandidateDto.From)
                    .ToArray(),
                request.AdoptedHighlights
                    .OrderBy(static item => item.OriginalCandidate.Id.ToString(),
                        StringComparer.Ordinal)
                    .Select(HighlightAdoptionDto.From)
                    .ToArray(),
                request.ThumbnailEvidence is null
                    ? null
                    : ThumbnailEvidenceDto.From(request.ThumbnailEvidence),
                request.ActorSid,
                Timestamp(request.CreatedAt),
                request.ChangeReason),
            new ExpectedDto(
                applicationDigest.Hex,
                plan.Id.ToString(),
                plan.PlanDigest.Hex,
                plan.InputDigests.Single(static input =>
                    input.Kind == StandardPlanInputKind.AutoDecision)
                    .ContentDigest.Hex,
                parentTimelineMap.Id.ToString(),
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                    parentTimelineMap).Hex,
                childWithoutEvidence.Id.ToString(),
                childWithoutEvidence.ContentDigest.Hex,
                childTimelineMap.Id.ToString(),
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                    childTimelineMap).Hex));
        return SerializeCanonical(document);
    }

    internal static ContentDigest ReadPriorAutoDecisionDigest(
        CompositionVersion parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        CompositionEvidenceEnvelope envelope = parent.Evidence
            ?? throw new FormatException(
                "Prior standard-composition evidence is missing.");
        if (!string.Equals(
                envelope.Kind,
                CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                StringComparison.Ordinal))
        {
            throw new FormatException(
                "Prior standard-composition evidence has the wrong kind.");
        }

        EvidenceDocumentDto document = DeserializeCanonical(
            envelope.CanonicalJson);
        if (!string.Equals(
                document.SchemaVersion,
                StandardCompositionOrchestrator.EvidenceSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new FormatException(
                "Prior standard-composition evidence has an unsupported schema version.");
        }

        if (ReadId<CompositionVersionIdTag>(document.Expected.ChildVersionId)
            != parent.Id
            || ReadDigest(document.Expected.ChildContentDigest)
            != parent.ContentDigest)
        {
            throw new FormatException(
                "Prior standard-composition evidence does not belong to its parent version.");
        }

        return ReadDigest(document.Expected.AutoDecisionDigest);
    }

    internal static ContentDigest ComputeEvaluationInputDigest(
        SourceDisplayGeometry sourceDisplayGeometry,
        IReadOnlyList<TopicMetadataRevision> topicMetadataRevisions,
        IReadOnlyList<EmphasisSuggestionCandidate> candidates,
        IReadOnlyList<HighlightSuggestionAdoptionResult> adoptedHighlights,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDisplayGeometry);
        ArgumentNullException.ThrowIfNull(topicMetadataRevisions);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(adoptedHighlights);
        cancellationToken.ThrowIfCancellationRequested();

        var topicDtos = new List<TopicMetadataRevisionDto>(
            topicMetadataRevisions.Count);
        foreach (TopicMetadataRevision item in topicMetadataRevisions.OrderBy(
            static value => value.Id.ToString(),
            StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            topicDtos.Add(TopicMetadataRevisionDto.From(item));
        }

        var candidateDtos = new List<EmphasisCandidateDto>(candidates.Count);
        foreach (EmphasisSuggestionCandidate item in candidates
            .OrderBy(static value => value.Payload.SourceSpan.AssetId.ToString(),
                StringComparer.Ordinal)
            .ThenBy(static value => value.Payload.SourceSpan.StreamId,
                StringComparer.Ordinal)
            .ThenBy(static value => value.Payload.SourceSpan.Range.Start)
            .ThenBy(static value => value.Payload.SourceSpan.Range.End)
            .ThenBy(static value => value.Id.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateDtos.Add(EmphasisCandidateDto.From(item));
        }

        var adoptionDtos = new List<HighlightAdoptionDto>(
            adoptedHighlights.Count);
        foreach (HighlightSuggestionAdoptionResult item in adoptedHighlights
            .OrderBy(static value => value.OriginalCandidate.Id.ToString(),
                StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            adoptionDtos.Add(HighlightAdoptionDto.From(item));
        }

        var document = new EvaluationInputDto(
            new SourceGeometryDto(
                sourceDisplayGeometry.Width,
                sourceDisplayGeometry.Height),
            topicDtos.ToArray(),
            candidateDtos.ToArray(),
            adoptionDtos.ToArray());
        string canonicalJson = SerializeCanonical(document);
        return StandardCompositionCanonical.ComputeDigest(
            "hubradio.standard-evaluation-input.v1",
            [canonicalJson]);
    }

    internal static ValidatedStandardCompositionEvidence ValidateForCheckpoint(
        CompositionVersion child,
        CompositionVersion parent) =>
        ValidateLoaded(child, parent);

    internal static ValidatedStandardCompositionEvidence ValidateLoaded(
        CompositionVersion child,
        CompositionVersion parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        try
        {
            CompositionEvidenceEnvelope envelope = child.Evidence
                ?? throw new FormatException(
                    "Standard-composition evidence is missing.");
            if (!string.Equals(
                    envelope.Kind,
                    CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The composition evidence kind is not standardCompositionPlan.");
            }

            EvidenceDocumentDto document = DeserializeCanonical(
                envelope.CanonicalJson);
            if (!string.Equals(
                    document.SchemaVersion,
                    StandardCompositionOrchestrator.EvidenceSchemaVersion,
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The standard-composition evidence schema version is unsupported.");
            }

            if (ReadId<CompositionVersionIdTag>(document.Input.ParentVersionId)
                    != parent.Id
                || ReadDigest(document.Input.ParentContentDigest)
                    != parent.ContentDigest
                || child.ParentVersionId != parent.Id)
            {
                throw new FormatException(
                    "The standard-composition evidence does not match its stored parent.");
            }

            StandardCompositionOrchestrationRequest request = RebuildRequest(
                document.Input,
                parent);
            StandardCompositionPreparation replay =
                StandardCompositionOrchestrator.Prepare(request);
            if (!string.Equals(
                    CompositionSnapshotJson.WriteVersion(replay.Child),
                    CompositionSnapshotJson.WriteVersion(child),
                    StringComparison.Ordinal)
                || !string.Equals(
                    envelope.CanonicalJson,
                    replay.Child.Evidence?.CanonicalJson,
                    StringComparison.Ordinal)
                || envelope.ContentDigest
                    != replay.Child.Evidence?.ContentDigest)
            {
                throw new FormatException(
                    "The stored standard-composition child does not match semantic replay.");
            }

            return new ValidatedStandardCompositionEvidence(envelope, replay);
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                and not StackOverflowException
                and not AccessViolationException)
        {
            throw new FormatException(
                "The standard-composition evidence is invalid.",
                exception);
        }
    }

    private static StandardCompositionOrchestrationRequest RebuildRequest(
        InputDto input,
        CompositionVersion parent)
    {
        DefaultSettingsProfile settingsProfile =
            DefaultSettingsProfile.ParseCanonical(input.SettingsProfileJson);
        DefaultSettingsSnapshot settings =
            DefaultSettingsSnapshot.ParseCanonical(
                input.SettingsSnapshotJson,
                settingsProfile);
        byte[] landscapeJson = StrictUtf8.GetBytes(
            input.MeasuredLandscapeProfileJson);
        byte[] verticalJson = StrictUtf8.GetBytes(
            input.MeasuredVerticalProfileJson);
        BundledRenderProfileDefinition measuredLandscape =
            BundledRenderProfileLoader.Load(landscapeJson);
        BundledRenderProfileDefinition measuredVertical =
            BundledRenderProfileLoader.Load(verticalJson);
        RationalNumber sourceFrameRate = input.SourceFrameRate.ToDomain();
        if (sourceFrameRate.Numerator <= 0)
        {
            throw new FormatException(
                "The source frame rate must be positive.");
        }

        var sourceGeometry = new SourceDisplayGeometry(
            input.SourceDisplayGeometry.Width,
            input.SourceDisplayGeometry.Height);
        BundledRenderProfileDefinition landscapeProfile =
            StandardRenderProfileFactory.CreateLongForm(
                settingsProfile,
                settings,
                measuredLandscape,
                measuredVertical,
                sourceFrameRate,
                sourceGeometry);
        LongFormCompositionRequest longForm = input.LongFormRequest.ToDomain(
            landscapeProfile);
        WorkingCopy workingCopy = input.WorkingCopy.ToDomain(parent);
        TopicMetadataRevision[] topics = input.TopicMetadataRevisions
            .Select(static item => item.ToDomain())
            .ToArray();
        EmphasisSuggestionCandidate[] candidates = input.Candidates
            .Select(static item => item.ToDomain())
            .ToArray();
        Dictionary<EntityId<SuggestionIdTag>, EmphasisSuggestionCandidate>
            candidatesById = candidates.ToDictionary(static item => item.Id);
        HighlightSuggestionAdoptionResult[] adoptions = input.AdoptedHighlights
            .Select(item => item.ToDomain(candidatesById))
            .ToArray();
        return new StandardCompositionOrchestrationRequest(
            parent,
            workingCopy,
            input.WorkingCopy.Revision,
            input.ProjectStoreState.ToDomain(),
            settingsProfile,
            settings,
            landscapeJson,
            verticalJson,
            longForm,
            ReadId<TimelineMapIdTag>(input.ParentTimelineMapId),
            sourceGeometry,
            sourceFrameRate,
            ReadDigest(input.ToolDigest),
            ReadDigest(input.ModelDigest),
            input.MusicPlanning.ToDomain(),
            topics,
            candidates,
            adoptions,
            input.ActorSid,
            ReadTimestamp(input.CreatedAt),
            input.ChangeReason,
            input.ThumbnailEvidence?.ToDomain());
    }

    private static string SerializeCanonical<TDocument>(TDocument document)
    {
        var buffer = new BoundedBufferWriter(
            CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes);
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            JsonSerializer.Serialize(writer, document, JsonOptions);
            writer.Flush();
        }

        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(
            buffer.WrittenSpan);
        if (canonical.Length
            > CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes)
        {
            throw new FormatException(
                "Standard-composition evidence exceeds its UTF-8 resource-safety envelope.");
        }

        return StrictUtf8.GetString(canonical);
    }

    private sealed class BoundedBufferWriter : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _inner = new();
        private readonly int _maximumBytes;

        internal BoundedBufferWriter(int maximumBytes)
        {
            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            _maximumBytes = maximumBytes;
        }

        internal ReadOnlySpan<byte> WrittenSpan => _inner.WrittenSpan;

        public void Advance(int count)
        {
            if (count < 0 || count > _maximumBytes - _inner.WrittenCount)
            {
                throw TooLarge();
            }

            _inner.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            int length = GetLength(sizeHint);
            return _inner.GetMemory(length)[..length];
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int length = GetLength(sizeHint);
            return _inner.GetSpan(length)[..length];
        }

        private int GetLength(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            int remaining = _maximumBytes - _inner.WrittenCount;
            int required = Math.Max(sizeHint, 1);
            if (required > remaining)
            {
                throw TooLarge();
            }

            return Math.Min(remaining, Math.Max(required, 4096));
        }

        private static FormatException TooLarge() =>
            new(
                "Standard-composition evidence exceeds its UTF-8 resource-safety envelope.");
    }

    private static EvidenceDocumentDto DeserializeCanonical(string canonicalJson)
    {
        byte[] utf8 = StrictUtf8.GetBytes(canonicalJson);
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
        if (!utf8.AsSpan().SequenceEqual(canonical))
        {
            throw new FormatException(
                "Standard-composition evidence must use canonical JSON.");
        }

        EvidenceDocumentDto document = JsonSerializer.Deserialize<
            EvidenceDocumentDto>(canonical, JsonOptions)
            ?? throw new FormatException(
                "Standard-composition evidence must contain an object.");
        if (!string.Equals(
                SerializeCanonical(document),
                canonicalJson,
                StringComparison.Ordinal))
        {
            throw new FormatException(
                "Standard-composition evidence does not match its closed canonical shape.");
        }

        return document;
    }

    private static string CanonicalEmbeddedJson(ReadOnlySpan<byte> utf8) =>
        StrictUtf8.GetString(CanonicalJson.CanonicalizeToUtf8(utf8));

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadTimestamp(string value)
    {
        DateTimeOffset parsed = DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        string canonical = Timestamp(parsed);
        if (!string.Equals(value, canonical, StringComparison.Ordinal))
        {
            throw new FormatException("A timestamp is not canonical UTC.");
        }

        return parsed;
    }

    private static DateTimeOffset? ReadOptionalTimestamp(string? value) =>
        value is null ? null : ReadTimestamp(value);

    private static EntityId<TTag> ReadId<TTag>(string value)
    {
        EntityId<TTag> id = EntityId<TTag>.Parse(value);
        if (!string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw new FormatException("An entity ID is not canonical lowercase UUID text.");
        }

        return id;
    }

    private static EntityId<TTag>? ReadOptionalId<TTag>(string? value) =>
        value is null ? null : ReadId<TTag>(value);

    private static ContentDigest ReadDigest(string value)
    {
        var digest = new ContentDigest(value);
        if (!string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw new FormatException("A content digest is not canonical lowercase text.");
        }

        return digest;
    }

    private static ContentDigest? ReadOptionalDigest(string? value) =>
        value is null ? null : ReadDigest(value);

    private static TEnum ReadEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed)
            || !Enum.IsDefined(parsed)
            || !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw new FormatException($"Unknown {typeof(TEnum).Name} value.");
        }

        return parsed;
    }

    private sealed class CanonicalInt32Converter : JsonConverter<int>
    {
        public override int Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            decimal value = ReadIntegralDecimal(ref reader);
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new JsonException();
            }

            return decimal.ToInt32(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            int value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }

    private sealed class CanonicalInt64Converter : JsonConverter<long>
    {
        public override long Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            decimal value = ReadIntegralDecimal(ref reader);
            if (value < long.MinValue || value > long.MaxValue)
            {
                throw new JsonException();
            }

            return decimal.ToInt64(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            long value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }

    private static decimal ReadIntegralDecimal(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number
            || !reader.TryGetDecimal(out decimal value)
            || value != decimal.Truncate(value))
        {
            throw new JsonException();
        }

        return value;
    }

    private sealed record EvaluationInputDto(
        SourceGeometryDto SourceDisplayGeometry,
        TopicMetadataRevisionDto[] TopicMetadataRevisions,
        EmphasisCandidateDto[] Candidates,
        HighlightAdoptionDto[] AdoptedHighlights);

    private sealed record EvidenceDocumentDto(
        string SchemaVersion,
        InputDto Input,
        ExpectedDto Expected);

    private sealed record InputDto(
        string ParentVersionId,
        string ParentContentDigest,
        WorkingCopyDto WorkingCopy,
        ProjectStoreStateDto ProjectStoreState,
        string SettingsProfileJson,
        string SettingsSnapshotJson,
        string MeasuredLandscapeProfileJson,
        string MeasuredVerticalProfileJson,
        LongFormRequestDto LongFormRequest,
        string ParentTimelineMapId,
        SourceGeometryDto SourceDisplayGeometry,
        RationalDto SourceFrameRate,
        string ToolDigest,
        string ModelDigest,
        MusicPlanningDto MusicPlanning,
        TopicMetadataRevisionDto[] TopicMetadataRevisions,
        EmphasisCandidateDto[] Candidates,
        HighlightAdoptionDto[] AdoptedHighlights,
        ThumbnailEvidenceDto? ThumbnailEvidence,
        string ActorSid,
        string CreatedAt,
        string ChangeReason);

    private sealed record ExpectedDto(
        string ApplicationDigest,
        string StandardPlanId,
        string StandardPlanDigest,
        string AutoDecisionDigest,
        string ParentTimelineMapId,
        string ParentTimelineMapDigest,
        string ChildVersionId,
        string ChildContentDigest,
        string ChildTimelineMapId,
        string ChildTimelineMapDigest);

    private sealed record SourceGeometryDto(int Width, int Height);

    private sealed record ProjectStoreStateDto(
        int Revision,
        string PayloadDigest,
        string UpdatedAt)
    {
        internal static ProjectStoreStateDto From(
            StandardProjectStoreState value) =>
            new(
                value.Revision,
                value.PayloadDigest.Hex,
                Timestamp(value.UpdatedAt));

        internal StandardProjectStoreState ToDomain() =>
            new(
                Revision,
                ReadDigest(PayloadDigest),
                ReadTimestamp(UpdatedAt));
    }

    private sealed record ThumbnailEvidenceDto(
        string SelectionRuleVersion,
        string ProjectId,
        string SourceAssetId,
        string SourceArtifactId,
        string SourceFileName,
        string SourceContentDigest,
        long SourceByteSize,
        int SourceStreamIndex,
        string SourcePtsIndexDigest,
        string ProxyFrameMapDigest,
        string ProxyArtifactId,
        string ProxyContentDigest,
        long ProxyByteSize,
        string ProxyPtsIndexDigest,
        string SpriteImageArtifactId,
        string SpriteImageDigest,
        long SpriteImageByteSize,
        string SpriteIndexArtifactId,
        string SpriteIndexDigest,
        long SpriteIndexByteSize,
        string PreviewProfileVersion,
        ThumbnailFrameDto[] Frames)
    {
        internal static ThumbnailEvidenceDto From(
            StandardThumbnailEvidence value) =>
            new(
                StandardThumbnailEvidence.SelectionRuleVersion,
                value.ProjectId.ToString(),
                value.SourceAssetId.ToString(),
                value.SourceArtifactId.ToString(),
                value.SourceFileName,
                value.SourceContentDigest.Hex,
                value.SourceByteSize,
                value.SourceStreamIndex,
                value.SourcePtsIndexDigest.Hex,
                value.ProxyFrameMapDigest.Hex,
                value.ProxyArtifactId.ToString(),
                value.ProxyContentDigest.Hex,
                value.ProxyByteSize,
                value.ProxyPtsIndexDigest.Hex,
                value.SpriteImageArtifactId.ToString(),
                value.SpriteImageDigest.Hex,
                value.SpriteImageByteSize,
                value.SpriteIndexArtifactId.ToString(),
                value.SpriteIndexDigest.Hex,
                value.SpriteIndexByteSize,
                value.PreviewProfileVersion,
                value.Frames.Select(ThumbnailFrameDto.From).ToArray());

        internal StandardThumbnailEvidence ToDomain()
        {
            if (!string.Equals(
                    SelectionRuleVersion,
                    StandardThumbnailEvidence.SelectionRuleVersion,
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The standard thumbnail selection-rule version is unsupported.");
            }

            return new StandardThumbnailEvidence(
                ReadId<ProjectIdTag>(ProjectId),
                ReadId<AssetIdTag>(SourceAssetId),
                ReadId<ArtifactIdTag>(SourceArtifactId),
                SourceFileName,
                ReadDigest(SourceContentDigest),
                SourceByteSize,
                SourceStreamIndex,
                ReadDigest(SourcePtsIndexDigest),
                ReadDigest(ProxyFrameMapDigest),
                ReadId<ArtifactIdTag>(ProxyArtifactId),
                ReadDigest(ProxyContentDigest),
                ProxyByteSize,
                ReadDigest(ProxyPtsIndexDigest),
                ReadId<ArtifactIdTag>(SpriteImageArtifactId),
                ReadDigest(SpriteImageDigest),
                SpriteImageByteSize,
                ReadId<ArtifactIdTag>(SpriteIndexArtifactId),
                ReadDigest(SpriteIndexDigest),
                SpriteIndexByteSize,
                PreviewProfileVersion,
                Frames.Select(static frame => frame.ToDomain()).ToArray());
        }
    }

    private sealed record ThumbnailFrameDto(
        int SpriteOrdinal,
        int ProxyFrameSequence,
        TimeDto ProxyPresentationTime,
        int SourceFrameSequence,
        TimeDto SourcePresentationTime,
        TimeDto SourceDuration,
        bool SourceKeyFrame)
    {
        internal static ThumbnailFrameDto From(
            StandardThumbnailFrameEvidence value) =>
            new(
                value.SpriteOrdinal,
                value.ProxyFrameSequence,
                TimeDto.From(value.ProxyPresentationTime),
                value.SourceFrameSequence,
                TimeDto.From(value.SourcePresentationTime),
                TimeDto.From(value.SourceDuration),
                value.SourceKeyFrame);

        internal StandardThumbnailFrameEvidence ToDomain() =>
            new(
                SpriteOrdinal,
                ProxyFrameSequence,
                ProxyPresentationTime.ToDomain(),
                SourceFrameSequence,
                SourcePresentationTime.ToDomain(),
                SourceDuration.ToDomain(),
                SourceKeyFrame);
    }

    private sealed record MusicPlanningDto(
        string CatalogDigest,
        MusicCueAssignmentDto[] Assignments,
        string[] Warnings,
        MusicPolicyEvaluationDto[] PolicyEvaluations)
    {
        internal static MusicPlanningDto From(MusicPlanningResult value) =>
            new(
                value.CatalogDigest.Hex,
                value.Assignments
                    .OrderBy(static item => item.CueDigest.Hex,
                        StringComparer.Ordinal)
                    .Select(MusicCueAssignmentDto.From)
                    .ToArray(),
                value.Warnings
                    .Order()
                    .Select(static item => item.ToString())
                    .ToArray(),
                value.PolicyEvaluations
                    .OrderBy(static item => item.Domain)
                    .Select(MusicPolicyEvaluationDto.From)
                    .ToArray());

        internal MusicPlanningResult ToDomain() =>
            new(
                ReadDigest(CatalogDigest),
                Array.AsReadOnly(Assignments
                    .Select(static item => item.ToDomain())
                    .ToArray()),
                Array.AsReadOnly(Warnings
                    .Select(ReadEnum<MusicCatalogWarning>)
                    .ToArray()),
                Array.AsReadOnly(PolicyEvaluations
                    .Select(static item => item.ToDomain())
                    .ToArray()));
    }

    private sealed record MusicCueAssignmentDto(
        string CueDigest,
        string? TrackDigest,
        string Reason,
        int? RelativeGainDecibels,
        bool Loop,
        bool Stretch)
    {
        internal static MusicCueAssignmentDto From(
            MusicCueAssignment value) =>
            new(
                value.CueDigest.Hex,
                value.TrackDigest?.Hex,
                value.Reason.ToString(),
                value.RelativeGainDecibels,
                value.Loop,
                value.Stretch);

        internal MusicCueAssignment ToDomain() =>
            new(
                ReadDigest(CueDigest),
                ReadOptionalDigest(TrackDigest),
                ReadEnum<MusicAssignmentReason>(Reason),
                RelativeGainDecibels,
                Loop,
                Stretch);
    }

    private sealed record MusicPolicyEvaluationDto(
        string Domain,
        string Status)
    {
        internal static MusicPolicyEvaluationDto From(
            MusicPolicyEvaluation value) =>
            new(value.Domain.ToString(), value.Status.ToString());

        internal MusicPolicyEvaluation ToDomain() =>
            new(
                ReadEnum<MusicPolicyDomain>(Domain),
                ReadEnum<MusicPolicyEvaluationStatus>(Status));
    }

    private sealed record WorkingCopyDto(
        string Id,
        string ProjectId,
        string BaseVersionId,
        string CreatedAt,
        string UpdatedAt,
        int Revision)
    {
        internal static WorkingCopyDto From(WorkingCopy value, int revision) =>
            new(
                value.Id.ToString(),
                value.ProjectId.ToString(),
                value.BaseVersionId?.ToString()
                    ?? throw new ArgumentException(
                        "A standard-composition working copy requires a base version."),
                Timestamp(value.CreatedAt),
                Timestamp(value.UpdatedAt),
                revision);

        internal WorkingCopy ToDomain(CompositionVersion parent)
        {
            if (Revision < 0
                || ReadId<ProjectIdTag>(ProjectId) != parent.ProjectId
                || ReadId<CompositionVersionIdTag>(BaseVersionId) != parent.Id)
            {
                throw new FormatException(
                    "The working-copy evidence does not match the parent.");
            }

            return new WorkingCopy(
                ReadId<WorkingCopyIdTag>(Id),
                parent.ProjectId,
                parent.Id,
                ReadTimestamp(CreatedAt),
                ReadTimestamp(UpdatedAt),
                parent.Elements);
        }
    }

    private sealed record ProjectDto(
        string Id,
        string ContentName,
        string Purpose,
        string Audience,
        string PublicationScope,
        string? Deadline,
        AssetDto[] Assets,
        PerformerDto[] Performers)
    {
        internal static ProjectDto From(Project value) =>
            new(
                value.Id.ToString(),
                value.ContentName,
                value.Purpose,
                value.Audience,
                value.PublicationScope.ToString(),
                value.Deadline is null ? null : Timestamp(value.Deadline.Value),
                value.Assets
                    .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal)
                    .Select(AssetDto.From)
                    .ToArray(),
                value.Performers
                    .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal)
                    .Select(PerformerDto.From)
                    .ToArray());

        internal Project ToDomain() =>
            new(
                ReadId<ProjectIdTag>(Id),
                ContentName,
                Purpose,
                Audience,
                ReadEnum<PublicationScope>(PublicationScope),
                ReadOptionalTimestamp(Deadline),
                Assets.Select(static item => item.ToDomain()).ToArray(),
                Performers.Select(static item => item.ToDomain()).ToArray());
    }

    private sealed record AssetDto(
        string Id,
        string FileName,
        string MediaType,
        string ContentDigest,
        long ByteSize,
        RightsDto Rights)
    {
        internal static AssetDto From(Asset value) =>
            new(
                value.Id.ToString(),
                value.FileName,
                value.MediaType,
                value.ContentDigest.Hex,
                value.ByteSize,
                RightsDto.From(value.Rights));

        internal Asset ToDomain() =>
            new(
                ReadId<AssetIdTag>(Id),
                FileName,
                MediaType,
                ReadDigest(ContentDigest),
                ByteSize,
                Rights.ToDomain());
    }

    private sealed record RightsDto(
        string Status,
        string? EvidenceReference,
        string AllowedUse,
        bool ExternalPublicationAllowed)
    {
        internal static RightsDto From(RightsRecord value) =>
            new(
                value.Status.ToString(),
                value.EvidenceReference,
                value.AllowedUse,
                value.ExternalPublicationAllowed);

        internal RightsRecord ToDomain() =>
            new(
                ReadEnum<RightsStatus>(Status),
                EvidenceReference,
                AllowedUse,
                ExternalPublicationAllowed);
    }

    private sealed record PerformerDto(
        string Id,
        string DisplayName,
        string Role,
        string? TitleConfirmedAt)
    {
        internal static PerformerDto From(Performer value) =>
            new(
                value.Id.ToString(),
                value.DisplayName,
                value.Role,
                value.TitleConfirmedAt is null
                    ? null
                    : Timestamp(value.TitleConfirmedAt.Value));

        internal Performer ToDomain() =>
            new(
                ReadId<PerformerIdTag>(Id),
                DisplayName,
                Role,
                ReadOptionalTimestamp(TitleConfirmedAt));
    }

    private sealed record LongFormRequestDto(
        string PlanId,
        ProjectDto Project,
        ProvenanceDto Provenance,
        AppliedTemplateDto Template,
        string ProfileBindingDigest,
        string Destination,
        SpeakerHistoryDto? SpeakerAssignments,
        LongFormElementDto PreRollMargin,
        LongFormElementDto Opening,
        LongFormElementDto ProgramName,
        LongFormElementDto EpisodeNumber,
        LongFormElementDto EpisodeTitle,
        LongFormElementDto PerformerIntroduction,
        LongFormElementDto PublicationScopeIndicator,
        LongFormElementDto Body,
        LongFormTopicDto[] Topics,
        LongFormElementDto Summary,
        LongFormCallToActionDto[] CallToActions,
        LongFormElementDto Ending,
        LongFormElementDto PostRollMargin)
    {
        internal static LongFormRequestDto From(
            LongFormCompositionRequest value) =>
            new(
                value.PlanId.ToString(),
                ProjectDto.From(value.Project),
                ProvenanceDto.From(value.Provenance),
                AppliedTemplateDto.From(value.Template),
                StandardCompositionCanonical.ComputeProfileBindingDigest(
                    value.Profile).Hex,
                value.Destination.ToString(),
                value.SpeakerAssignments is null
                    ? null
                    : SpeakerHistoryDto.From(value.SpeakerAssignments),
                LongFormElementDto.From(value.PreRollMargin),
                LongFormElementDto.From(value.Opening),
                LongFormElementDto.From(value.ProgramName),
                LongFormElementDto.From(value.EpisodeNumber),
                LongFormElementDto.From(value.EpisodeTitle),
                LongFormElementDto.From(value.PerformerIntroduction),
                LongFormElementDto.From(value.PublicationScopeIndicator),
                LongFormElementDto.From(value.Body),
                value.Topics
                    .OrderBy(static item => item.TopicRange.Range.Start)
                    .ThenBy(static item => item.TopicRange.Range.End)
                    .ThenBy(static item => item.Element.Id.ToString(),
                        StringComparer.Ordinal)
                    .Select(LongFormTopicDto.From)
                    .ToArray(),
                LongFormElementDto.From(value.Summary),
                value.CallToActions
                    .OrderBy(static item => item.Destination)
                    .Select(LongFormCallToActionDto.From)
                    .ToArray(),
                LongFormElementDto.From(value.Ending),
                LongFormElementDto.From(value.PostRollMargin));

        internal LongFormCompositionRequest ToDomain(
            BundledRenderProfileDefinition profile)
        {
            if (ReadDigest(ProfileBindingDigest)
                != StandardCompositionCanonical.ComputeProfileBindingDigest(
                    profile))
            {
                throw new FormatException(
                    "The long-form profile binding does not match measured-profile derivation.");
            }

            Project project = Project.ToDomain();
            return new LongFormCompositionRequest(
                ReadId<LongFormPlanIdTag>(PlanId),
                project,
                Provenance.ToDomain(),
                Template.ToDomain(),
                profile,
                ReadEnum<LongFormDestination>(Destination),
                SpeakerAssignments?.ToDomain(),
                PreRollMargin.ToDomain(),
                Opening.ToDomain(),
                ProgramName.ToDomain(),
                EpisodeNumber.ToDomain(),
                EpisodeTitle.ToDomain(),
                PerformerIntroduction.ToDomain(),
                PublicationScopeIndicator.ToDomain(),
                Body.ToDomain(),
                Topics.Select(static item => item.ToDomain()).ToArray(),
                Summary.ToDomain(),
                CallToActions.Select(static item => item.ToDomain()).ToArray(),
                Ending.ToDomain(),
                PostRollMargin.ToDomain());
        }
    }

    private sealed record ProvenanceDto(
        string ProjectId,
        string TranscriptRevisionId,
        string TranscriptRevisionDigest,
        SourceSpanDto[] SourceSpans,
        string[] TrustedEditorialRecordIds)
    {
        internal static ProvenanceDto From(LongFormProvenanceSnapshot value) =>
            new(
                value.ProjectId.ToString(),
                value.TranscriptRevisionId.ToString(),
                value.TranscriptRevisionDigest.Hex,
                value.SourceSpans
                    .OrderBy(static item => item.AssetId.ToString(),
                        StringComparer.Ordinal)
                    .ThenBy(static item => item.StreamId, StringComparer.Ordinal)
                    .ThenBy(static item => item.Range.Start)
                    .ThenBy(static item => item.Range.End)
                    .ThenBy(static item => item.Id.ToString(),
                        StringComparer.Ordinal)
                    .Select(SourceSpanDto.From)
                    .ToArray(),
                value.TrustedEditorialRecordIds
                    .Select(static item => item.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray());

        internal LongFormProvenanceSnapshot ToDomain() =>
            new(
                ReadId<ProjectIdTag>(ProjectId),
                ReadId<TranscriptRevisionIdTag>(TranscriptRevisionId),
                ReadDigest(TranscriptRevisionDigest),
                SourceSpans.Select(static item => item.ToDomain()).ToArray(),
                TrustedEditorialRecordIds
                    .Select(ReadId<EditorialRecordIdTag>)
                    .ToArray());
    }

    private sealed record AppliedTemplateDto(
        string SeriesId,
        string TemplateId,
        string TemplateName,
        string TemplateVersionDigest,
        string Kind,
        TemplateParameterDto[] Parameters)
    {
        internal static AppliedTemplateDto From(AppliedBrandTemplate value) =>
            new(
                value.SeriesId.ToString(),
                value.Reference.Id.ToString(),
                value.Reference.Name,
                value.Reference.VersionDigest.Hex,
                value.Kind.ToString(),
                value.Parameters
                    .OrderBy(static item => item.Name, StringComparer.Ordinal)
                    .Select(TemplateParameterDto.From)
                    .ToArray());

        internal AppliedBrandTemplate ToDomain()
        {
            TemplateParameterDefinition[] definitions = Parameters
                .Select(item => new TemplateParameterDefinition(
                    item.Name,
                    item.Value.ToDomain(),
                    editorOverrideAllowed: false))
                .ToArray();
            return new BrandTemplateVersion(
                ReadId<SeriesIdTag>(SeriesId),
                new TemplateReference(
                    ReadId<TemplateIdTag>(TemplateId),
                    TemplateName,
                    ReadDigest(TemplateVersionDigest)),
                ReadEnum<BrandTemplateKind>(Kind),
                definitions).ApplyEditorOverrides([]);
        }
    }

    private sealed record TemplateParameterDto(
        string Name,
        TemplateValueDto Value)
    {
        internal static TemplateParameterDto From(
            TemplateParameterAssignment value) =>
            new(value.Name, TemplateValueDto.From(value.Value));
    }

    private sealed record TemplateValueDto(
        string Kind,
        string? Text,
        long? Integer,
        bool? Boolean,
        RationalDto? Rational,
        string? AssetId,
        string? AssetDigest)
    {
        internal static TemplateValueDto From(TemplateParameterValue value) =>
            new(
                value.Kind.ToString(),
                value.TextValue,
                value.IntegerValue,
                value.BooleanValue,
                value.RationalValue is null
                    ? null
                    : RationalDto.From(value.RationalValue.Value),
                value.AssetId?.ToString(),
                value.AssetDigest?.Hex);

        internal TemplateParameterValue ToDomain()
        {
            TemplateParameterValueKind kind =
                ReadEnum<TemplateParameterValueKind>(Kind);
            bool valid = kind switch
            {
                TemplateParameterValueKind.Text =>
                    Text is not null && Integer is null && Boolean is null
                    && Rational is null && AssetId is null && AssetDigest is null,
                TemplateParameterValueKind.Integer =>
                    Text is null && Integer is not null && Boolean is null
                    && Rational is null && AssetId is null && AssetDigest is null,
                TemplateParameterValueKind.Boolean =>
                    Text is null && Integer is null && Boolean is not null
                    && Rational is null && AssetId is null && AssetDigest is null,
                TemplateParameterValueKind.Rational =>
                    Text is null && Integer is null && Boolean is null
                    && Rational is not null && AssetId is null && AssetDigest is null,
                TemplateParameterValueKind.AssetReference =>
                    Text is null && Integer is null && Boolean is null
                    && Rational is null && AssetId is not null
                    && AssetDigest is not null,
                _ => false,
            };
            if (!valid)
            {
                throw new FormatException(
                    "A template parameter value has an invalid closed shape.");
            }

            return kind switch
            {
                TemplateParameterValueKind.Text =>
                    TemplateParameterValue.FromText(Text!),
                TemplateParameterValueKind.Integer =>
                    TemplateParameterValue.FromInteger(Integer!.Value),
                TemplateParameterValueKind.Boolean =>
                    TemplateParameterValue.FromBoolean(Boolean!.Value),
                TemplateParameterValueKind.Rational =>
                    TemplateParameterValue.FromRational(Rational!.ToDomain()),
                TemplateParameterValueKind.AssetReference =>
                    TemplateParameterValue.FromAssetReference(
                        ReadId<AssetIdTag>(AssetId!),
                        ReadDigest(AssetDigest!)),
                _ => throw new FormatException(
                    "Unknown template parameter value kind."),
            };
        }
    }

    private sealed record RationalDto(long Numerator, long Denominator)
    {
        internal static RationalDto From(RationalNumber value) =>
            new(value.Numerator, value.Denominator);

        internal RationalNumber ToDomain() =>
            new(Numerator, Denominator);
    }

    private sealed record LongFormElementDto(
        string Id,
        string Kind,
        bool Enabled,
        string? Text,
        RangeDto? OutputRange,
        OriginDto[] Origins,
        LongFormAssetReferenceDto[] Assets)
    {
        internal static LongFormElementDto From(LongFormElementInput value) =>
            new(
                value.Id.ToString(),
                value.Kind.ToString(),
                value.Enabled,
                value.Text,
                value.OutputRange is null
                    ? null
                    : RangeDto.From(value.OutputRange.Value),
                value.Origins
                    .OrderBy(static item => item.Kind)
                    .ThenBy(static item => OriginDto.Identity(item),
                        StringComparer.Ordinal)
                    .Select(OriginDto.From)
                    .ToArray(),
                value.Assets
                    .OrderBy(static item => item.Role)
                    .ThenBy(static item => item.AssetId.ToString(),
                        StringComparer.Ordinal)
                    .Select(LongFormAssetReferenceDto.From)
                    .ToArray());

        internal LongFormElementInput ToDomain() =>
            new(
                ReadId<LongFormElementIdTag>(Id),
                ReadEnum<LongFormElementKind>(Kind),
                Enabled,
                Text,
                OutputRange?.ToDomain(),
                Origins.Select(static item => item.ToDomain()).ToArray(),
                Assets.Select(static item => item.ToDomain()).ToArray());
    }

    private sealed record LongFormAssetReferenceDto(
        string Role,
        string AssetId,
        string AssetDigest)
    {
        internal static LongFormAssetReferenceDto From(
            LongFormAssetReference value) =>
            new(
                value.Role.ToString(),
                value.AssetId.ToString(),
                value.AssetDigest.Hex);

        internal LongFormAssetReference ToDomain() =>
            new(
                ReadEnum<LongFormAssetRole>(Role),
                ReadId<AssetIdTag>(AssetId),
                ReadDigest(AssetDigest));
    }

    private sealed record LongFormTopicDto(
        LongFormElementDto Element,
        TopicRangeRevisionDto TopicRange,
        TopicMetadataRevisionDto TopicMetadata)
    {
        internal static LongFormTopicDto From(LongFormTopicInput value) =>
            new(
                LongFormElementDto.From(value.Element),
                TopicRangeRevisionDto.From(value.TopicRange),
                TopicMetadataRevisionDto.From(value.TopicMetadata));

        internal LongFormTopicInput ToDomain() =>
            new(
                Element.ToDomain(),
                TopicRange.ToDomain(),
                TopicMetadata.ToDomain());
    }

    private sealed record LongFormCallToActionDto(
        string Destination,
        LongFormElementDto Element)
    {
        internal static LongFormCallToActionDto From(
            LongFormCallToActionInput value) =>
            new(value.Destination.ToString(), LongFormElementDto.From(value.Element));

        internal LongFormCallToActionInput ToDomain() =>
            new(ReadEnum<LongFormDestination>(Destination), Element.ToDomain());
    }

    private sealed record SourceSpanDto(
        string Id,
        string AssetId,
        string StreamId,
        RangeDto Range,
        string SourceDigest,
        string? TranscriptRevisionId,
        string[] TranscriptTokenIds)
    {
        internal static SourceSpanDto From(SourceSpan value) =>
            new(
                value.Id.ToString(),
                value.AssetId.ToString(),
                value.StreamId,
                RangeDto.From(value.Range),
                value.SourceDigest.Hex,
                value.TranscriptRevisionId?.ToString(),
                value.TranscriptTokenIds
                    .Select(static item => item.ToString())
                    .ToArray());

        internal SourceSpan ToDomain() =>
            new(
                ReadId<SourceSpanIdTag>(Id),
                ReadId<AssetIdTag>(AssetId),
                StreamId,
                Range.ToDomain(),
                ReadDigest(SourceDigest),
                ReadOptionalId<TranscriptRevisionIdTag>(TranscriptRevisionId),
                TranscriptTokenIds
                    .Select(ReadId<TranscriptTokenIdTag>)
                    .ToArray());
    }

    private sealed record TimeDto(long Value, long Rate)
    {
        internal static TimeDto From(RationalTime value) =>
            new(value.Value, value.Rate);

        internal RationalTime ToDomain() => new(Value, Rate);
    }

    private sealed record RangeDto(TimeDto Start, TimeDto Duration)
    {
        internal static RangeDto From(TimeRange value) =>
            new(TimeDto.From(value.Start), TimeDto.From(value.Duration));

        internal TimeRange ToDomain() =>
            new(Start.ToDomain(), Duration.ToDomain());
    }

    private sealed record OriginDto(string Kind, string Id)
    {
        internal static OriginDto From(OriginRef value) =>
            value.Kind switch
            {
                OriginKind.SourceSpan when value.SourceSpanId is not null =>
                    new("sourceSpan", value.SourceSpanId.ToString()),
                OriginKind.Asset when value.AssetId is not null =>
                    new("asset", value.AssetId.ToString()),
                OriginKind.EditorialRecord
                    when value.EditorialRecordId is not null =>
                    new("editorialRecord", value.EditorialRecordId.ToString()),
                _ => throw new ArgumentException("An origin is invalid."),
            };

        internal static string Identity(OriginRef value) =>
            value.SourceSpanId?.ToString()
            ?? value.AssetId?.ToString()
            ?? value.EditorialRecordId?.ToString()
            ?? throw new ArgumentException("An origin is invalid.");

        internal OriginRef ToDomain() =>
            Kind switch
            {
                "sourceSpan" => OriginRef.FromSourceSpan(
                    ReadId<SourceSpanIdTag>(Id)),
                "asset" => OriginRef.FromAsset(ReadId<AssetIdTag>(Id)),
                "editorialRecord" => OriginRef.FromEditorialRecord(
                    ReadId<EditorialRecordIdTag>(Id)),
                _ => throw new FormatException("Unknown origin kind."),
            };
    }

    private sealed record TopicRangeRevisionDto(
        string TopicRangeId,
        string Id,
        string? ParentRevisionId,
        string SourceRevisionId,
        string SourceRevisionDigest,
        string? StartBoundarySuggestionId,
        string? EndBoundarySuggestionId,
        RangeDto Range,
        RangeDto? PreviousRange,
        string ActorSid,
        string CreatedAt,
        string Reason)
    {
        internal static TopicRangeRevisionDto From(TopicRangeRevision value) =>
            new(
                value.TopicRangeId.ToString(),
                value.Id.ToString(),
                value.ParentRevisionId?.ToString(),
                value.SourceRevisionId.ToString(),
                value.SourceRevisionDigest.Hex,
                value.StartBoundarySuggestionId?.ToString(),
                value.EndBoundarySuggestionId?.ToString(),
                RangeDto.From(value.Range),
                value.PreviousRange is null
                    ? null
                    : RangeDto.From(value.PreviousRange.Value),
                value.ActorSid,
                Timestamp(value.CreatedAt),
                value.Reason);

        internal TopicRangeRevision ToDomain() =>
            new(
                ReadId<TopicRangeIdTag>(TopicRangeId),
                ReadId<TopicRangeRevisionIdTag>(Id),
                ReadOptionalId<TopicRangeRevisionIdTag>(ParentRevisionId),
                ReadId<TranscriptRevisionIdTag>(SourceRevisionId),
                ReadDigest(SourceRevisionDigest),
                ReadOptionalId<SuggestionIdTag>(StartBoundarySuggestionId),
                ReadOptionalId<SuggestionIdTag>(EndBoundarySuggestionId),
                Range.ToDomain(),
                PreviousRange?.ToDomain(),
                ActorSid,
                ReadTimestamp(CreatedAt),
                Reason);
    }

    private sealed record TopicMetadataRevisionDto(
        string TopicMetadataId,
        string Id,
        string? ParentRevisionId,
        string ProjectId,
        string TopicRangeId,
        string TopicRangeRevisionId,
        RangeDto TopicRange,
        string SourceRevisionId,
        string SourceRevisionDigest,
        TopicMetadataDto Metadata,
        string ChangedField,
        string ActorSid,
        string CreatedAt,
        string Reason)
    {
        internal static TopicMetadataRevisionDto From(
            TopicMetadataRevision value) =>
            new(
                value.TopicMetadataId.ToString(),
                value.Id.ToString(),
                value.ParentRevisionId?.ToString(),
                value.ProjectId.ToString(),
                value.TopicRangeId.ToString(),
                value.TopicRangeRevisionId.ToString(),
                RangeDto.From(value.TopicRange),
                value.SourceRevisionId.ToString(),
                value.SourceRevisionDigest.Hex,
                TopicMetadataDto.From(value.Metadata),
                value.ChangedField.ToString(),
                value.ActorSid,
                Timestamp(value.CreatedAt),
                value.Reason);

        internal TopicMetadataRevision ToDomain() =>
            new(
                ReadId<TopicMetadataIdTag>(TopicMetadataId),
                ReadId<TopicMetadataRevisionIdTag>(Id),
                ReadOptionalId<TopicMetadataRevisionIdTag>(ParentRevisionId),
                ReadId<ProjectIdTag>(ProjectId),
                ReadId<TopicRangeIdTag>(TopicRangeId),
                ReadId<TopicRangeRevisionIdTag>(TopicRangeRevisionId),
                TopicRange.ToDomain(),
                ReadId<TranscriptRevisionIdTag>(SourceRevisionId),
                ReadDigest(SourceRevisionDigest),
                Metadata.ToDomain(),
                ReadEnum<TopicMetadataField>(ChangedField),
                ActorSid,
                ReadTimestamp(CreatedAt),
                Reason);
    }

    private sealed record TopicMetadataDto(
        string Title,
        string TitleOrigin,
        string? Summary,
        string SummaryOrigin,
        TopicMainSpeakerDto? MainSpeaker,
        string MainSpeakerOrigin,
        bool MainSpeakerTieUnresolved,
        string[] Keywords,
        string KeywordsOrigin,
        string[] ImportantStatementSourceSpanIds,
        string ImportantStatementsOrigin,
        string Audience,
        string AudienceOrigin,
        string[] PublicationCautions,
        string PublicationCautionsOrigin)
    {
        internal static TopicMetadataDto From(TopicMetadata value) =>
            new(
                value.Title,
                value.TitleOrigin.ToString(),
                value.Summary,
                value.SummaryOrigin.ToString(),
                value.MainSpeaker is null
                    ? null
                    : TopicMainSpeakerDto.From(value.MainSpeaker),
                value.MainSpeakerOrigin.ToString(),
                value.MainSpeakerTieUnresolved,
                value.Keywords.ToArray(),
                value.KeywordsOrigin.ToString(),
                value.ImportantStatementSourceSpanIds
                    .Select(static item => item.ToString())
                    .ToArray(),
                value.ImportantStatementsOrigin.ToString(),
                value.Audience,
                value.AudienceOrigin.ToString(),
                value.PublicationCautions.ToArray(),
                value.PublicationCautionsOrigin.ToString());

        internal TopicMetadata ToDomain() =>
            new(
                Title,
                ReadEnum<TopicMetadataValueOrigin>(TitleOrigin),
                Summary,
                ReadEnum<TopicMetadataValueOrigin>(SummaryOrigin),
                MainSpeaker?.ToDomain(),
                ReadEnum<TopicMetadataValueOrigin>(MainSpeakerOrigin),
                MainSpeakerTieUnresolved,
                Keywords,
                ReadEnum<TopicMetadataValueOrigin>(KeywordsOrigin),
                ImportantStatementSourceSpanIds
                    .Select(ReadId<SourceSpanIdTag>)
                    .ToArray(),
                ReadEnum<TopicMetadataValueOrigin>(ImportantStatementsOrigin),
                Audience,
                ReadEnum<TopicMetadataValueOrigin>(AudienceOrigin),
                PublicationCautions,
                ReadEnum<TopicMetadataValueOrigin>(PublicationCautionsOrigin));
    }

    private sealed record TopicMainSpeakerDto(
        string SpeakerId,
        string? PerformerId,
        bool DisplayVisible,
        string? DisplayText,
        bool TitleOrAffiliationConfirmationRequired,
        TimeDto OverlapDuration)
    {
        internal static TopicMainSpeakerDto From(
            TopicMainSpeakerSnapshot value) =>
            new(
                value.SpeakerId.ToString(),
                value.PerformerId?.ToString(),
                value.DisplayVisible,
                value.DisplayText,
                value.TitleOrAffiliationConfirmationRequired,
                TimeDto.From(value.OverlapDuration));

        internal TopicMainSpeakerSnapshot ToDomain() =>
            new(
                ReadId<TranscriptSpeakerIdTag>(SpeakerId),
                ReadOptionalId<PerformerIdTag>(PerformerId),
                DisplayVisible,
                DisplayText,
                TitleOrAffiliationConfirmationRequired,
                OverlapDuration.ToDomain());
    }

    private sealed record SpeakerHistoryDto(SpeakerRevisionDto[] Revisions)
    {
        internal static SpeakerHistoryDto From(SpeakerAssignmentHistory value) =>
            new(value.Revisions.Select(SpeakerRevisionDto.From).ToArray());

        internal SpeakerAssignmentHistory ToDomain() =>
            new(Revisions.Select(static item => item.ToDomain()).ToArray());
    }

    private sealed record SpeakerRevisionDto(
        string Id,
        string? ParentRevisionId,
        string ProjectId,
        string TranscriptRevisionId,
        string TranscriptRevisionDigest,
        string CreatedAt,
        string ActorSid,
        string Reason,
        SpeakerAssignmentDto[] Assignments,
        SpeakerChangeDto Change)
    {
        internal static SpeakerRevisionDto From(SpeakerAssignmentRevision value) =>
            new(
                value.Id.ToString(),
                value.ParentRevisionId?.ToString(),
                value.ProjectId.ToString(),
                value.TranscriptRevisionId.ToString(),
                value.TranscriptRevisionDigest.Hex,
                Timestamp(value.CreatedAt),
                value.ActorSid,
                value.Reason,
                value.Assignments.Select(SpeakerAssignmentDto.From).ToArray(),
                SpeakerChangeDto.From(value.Change));

        internal SpeakerAssignmentRevision ToDomain() =>
            new(
                ReadId<SpeakerAssignmentRevisionIdTag>(Id),
                ReadOptionalId<SpeakerAssignmentRevisionIdTag>(ParentRevisionId),
                ReadId<ProjectIdTag>(ProjectId),
                ReadId<TranscriptRevisionIdTag>(TranscriptRevisionId),
                ReadDigest(TranscriptRevisionDigest),
                ReadTimestamp(CreatedAt),
                ActorSid,
                Reason,
                Assignments.Select(static item => item.ToDomain()).ToArray(),
                Change.ToDomain());
    }

    private sealed record SpeakerAssignmentDto(
        string SpeakerId,
        string AnonymousLabel,
        string PerformerId,
        string FullName,
        string Surname,
        string Role,
        string? TitleConfirmedAt,
        string DisplayFormat)
    {
        internal static SpeakerAssignmentDto From(SpeakerAssignment value) =>
            new(
                value.SpeakerId.ToString(),
                value.AnonymousLabel,
                value.PerformerId.ToString(),
                value.FullName,
                value.Surname,
                value.Role,
                value.TitleConfirmedAt is null
                    ? null
                    : Timestamp(value.TitleConfirmedAt.Value),
                value.DisplayFormat.ToString());

        internal SpeakerAssignment ToDomain() =>
            new(
                ReadId<TranscriptSpeakerIdTag>(SpeakerId),
                AnonymousLabel,
                ReadId<PerformerIdTag>(PerformerId),
                FullName,
                Surname,
                Role,
                ReadOptionalTimestamp(TitleConfirmedAt),
                ReadEnum<SpeakerDisplayFormat>(DisplayFormat));
    }

    private sealed record SpeakerChangeDto(
        SpeakerAssignmentDto? PreviousAssignment,
        SpeakerAssignmentDto? CurrentAssignment)
    {
        internal static SpeakerChangeDto From(SpeakerAssignmentChange value) =>
            new(
                value.PreviousAssignment is null
                    ? null
                    : SpeakerAssignmentDto.From(value.PreviousAssignment),
                value.CurrentAssignment is null
                    ? null
                    : SpeakerAssignmentDto.From(value.CurrentAssignment));

        internal SpeakerAssignmentChange ToDomain() =>
            new(PreviousAssignment?.ToDomain(), CurrentAssignment?.ToDomain());
    }

    private sealed record EmphasisCandidateDto(
        SuggestionDto ProducerSuggestion,
        SuggestionDto CandidateSuggestion,
        string? ParentSuggestionId,
        string? ReplacementSuggestionId,
        SourceSpanDto SourceSpan,
        string SourceRevisionId,
        string SourceRevisionDigest,
        string TopicMetadataRevisionId,
        string RuleVersion,
        string Text,
        string TextSource,
        string[] Reasons,
        EmphasisTermDto[] TermEvidence)
    {
        internal static EmphasisCandidateDto From(
            EmphasisSuggestionCandidate value) =>
            new(
                SuggestionDto.From(value.Payload.Suggestion),
                SuggestionDto.From(value.Suggestion),
                value.ParentSuggestionId?.ToString(),
                value.ReplacementSuggestionId?.ToString(),
                SourceSpanDto.From(value.Payload.SourceSpan),
                value.Payload.SourceRevisionId.ToString(),
                value.Payload.SourceRevisionDigest.Hex,
                value.Payload.TopicMetadataRevisionId.ToString(),
                value.Payload.RuleVersion,
                value.Payload.Text,
                value.Payload.TextSource.ToString(),
                value.Payload.Reasons
                    .Order()
                    .Select(static item => item.ToString())
                    .ToArray(),
                value.Payload.TermEvidence
                    .OrderBy(static item => item.TermEntryId.ToString(),
                        StringComparer.Ordinal)
                    .Select(EmphasisTermDto.From)
                    .ToArray());

        internal EmphasisSuggestionCandidate ToDomain()
        {
            Suggestion producer = ProducerSuggestion.ToDomain();
            var payload = new EmphasisCandidate(
                producer,
                SourceSpan.ToDomain(),
                ReadId<TranscriptRevisionIdTag>(SourceRevisionId),
                ReadDigest(SourceRevisionDigest),
                ReadId<TopicMetadataRevisionIdTag>(TopicMetadataRevisionId),
                RuleVersion,
                Text,
                ReadEnum<EmphasisTextSourceKind>(TextSource),
                Reasons.Select(ReadEnum<EmphasisReasonKind>).ToArray(),
                TermEvidence.Select(static item => item.ToDomain()).ToArray());
            return new EmphasisSuggestionCandidate(
                payload,
                CandidateSuggestion.ToDomain(),
                ReadOptionalId<SuggestionIdTag>(ParentSuggestionId),
                ReadOptionalId<SuggestionIdTag>(ReplacementSuggestionId));
        }
    }

    private sealed record EmphasisTermDto(
        string TermEntryId,
        int TermEntryVersion,
        string WrittenForm)
    {
        internal static EmphasisTermDto From(EmphasisTermEvidence value) =>
            new(
                value.TermEntryId.ToString(),
                value.TermEntryVersion,
                value.WrittenForm);

        internal EmphasisTermEvidence ToDomain() =>
            new(
                ReadId<TermEntryIdTag>(TermEntryId),
                TermEntryVersion,
                WrittenForm);
    }

    private sealed record SuggestionDto(
        string Id,
        string ModelRunId,
        string Kind,
        string Status,
        OriginDto Origin,
        string Reason,
        double? Confidence,
        string? MissingContext,
        string? Warning)
    {
        internal static SuggestionDto From(Suggestion value) =>
            new(
                value.Id.ToString(),
                value.ModelRunId.ToString(),
                value.Kind.ToString(),
                value.Status.ToString(),
                OriginDto.From(value.Origin),
                value.Reason,
                value.Confidence,
                value.MissingContext,
                value.Warning);

        internal Suggestion ToDomain() =>
            new(
                ReadId<SuggestionIdTag>(Id),
                ReadId<ModelRunIdTag>(ModelRunId),
                ReadEnum<SuggestionKind>(Kind),
                ReadEnum<SuggestionStatus>(Status),
                Origin.ToDomain(),
                Reason,
                Confidence,
                MissingContext,
                Warning);
    }

    private sealed record HighlightAdoptionDto(
        string CandidateId,
        string CueId,
        RangeDto CueOutputRange,
        string? CueRegionName,
        SuggestionDecisionDto[] PriorDecisionHistory,
        string DecisionId,
        string ActorSid,
        string DecidedAt,
        string Reason)
    {
        internal static HighlightAdoptionDto From(
            HighlightSuggestionAdoptionResult value) =>
            new(
                value.OriginalCandidate.Id.ToString(),
                value.Cue.Id.ToString(),
                RangeDto.From(value.Cue.Range),
                value.Cue.RegionName,
                value.DecisionHistory
                    .Take(value.DecisionHistory.Count - 1)
                    .Select(SuggestionDecisionDto.From)
                    .ToArray(),
                value.DecisionRecord.Id.ToString(),
                value.DecisionRecord.ActorSid,
                Timestamp(value.DecisionRecord.DecidedAt),
                value.DecisionRecord.Reason);

        internal HighlightSuggestionAdoptionResult ToDomain(
            IReadOnlyDictionary<
                EntityId<SuggestionIdTag>,
                EmphasisSuggestionCandidate> candidates)
        {
            EntityId<SuggestionIdTag> candidateId =
                ReadId<SuggestionIdTag>(CandidateId);
            if (!candidates.TryGetValue(
                    candidateId,
                    out EmphasisSuggestionCandidate? candidate))
            {
                throw new FormatException(
                    "An adopted highlight does not identify an evidence candidate.");
            }

            return SuggestionService.AdoptHighlight(
                candidate,
                candidate.SourceDigest,
                ReadId<CueIdTag>(CueId),
                CueOutputRange.ToDomain(),
                CueRegionName,
                PriorDecisionHistory
                    .Select(static item => item.ToDomain())
                    .ToArray(),
                ReadId<SuggestionDecisionIdTag>(DecisionId),
                ActorSid,
                ReadTimestamp(DecidedAt),
                Reason);
        }
    }

    private sealed record SuggestionDecisionDto(
        string Id,
        string CandidateId,
        string ModelRunId,
        string Producer,
        string SuggestionKind,
        string Decision,
        string StatusAfterDecision,
        OriginDto Origin,
        string SourceDigest,
        string ActorSid,
        string DecidedAt,
        string Reason,
        string? ReplacementSuggestionId,
        SuggestionReferenceDto? AdoptedReference,
        string? ResultingTranscriptRevisionId)
    {
        internal static SuggestionDecisionDto From(
            SuggestionDecisionRecord value) =>
            new(
                value.Id.ToString(),
                value.CandidateId.ToString(),
                value.ModelRunId.ToString(),
                value.Producer.ToString(),
                value.SuggestionKind.ToString(),
                value.Decision.ToString(),
                value.StatusAfterDecision.ToString(),
                OriginDto.From(value.Origin),
                value.SourceDigest.Hex,
                value.ActorSid,
                Timestamp(value.DecidedAt),
                value.Reason,
                value.ReplacementSuggestionId?.ToString(),
                value.AdoptedReference is null
                    ? null
                    : SuggestionReferenceDto.From(value.AdoptedReference),
                value.ResultingTranscriptRevisionId?.ToString());

        internal SuggestionDecisionRecord ToDomain() =>
            new(
                ReadId<SuggestionDecisionIdTag>(Id),
                ReadId<SuggestionIdTag>(CandidateId),
                ReadId<ModelRunIdTag>(ModelRunId),
                ReadEnum<SuggestionProducerKind>(Producer),
                ReadEnum<SuggestionKind>(SuggestionKind),
                ReadEnum<SuggestionDecisionKind>(Decision),
                ReadEnum<SuggestionStatus>(StatusAfterDecision),
                Origin.ToDomain(),
                ReadDigest(SourceDigest),
                ActorSid,
                ReadTimestamp(DecidedAt),
                Reason,
                ReadOptionalId<SuggestionIdTag>(ReplacementSuggestionId),
                AdoptedReference?.ToDomain(),
                ReadOptionalId<TranscriptRevisionIdTag>(
                    ResultingTranscriptRevisionId));
    }

    private sealed record SuggestionReferenceDto(
        string SuggestionId,
        OriginDto Origin)
    {
        internal static SuggestionReferenceDto From(SuggestionReference value) =>
            new(value.SuggestionId.ToString(), OriginDto.From(value.Origin));

        internal SuggestionReference ToDomain() =>
            SuggestionReference.FromPersistedReference(
                ReadId<SuggestionIdTag>(SuggestionId),
                Origin.ToDomain());
    }
}
