using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
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

namespace HubRadio.App.Delivery;

public sealed class AncillaryArtifactExportIdTag
{
}

public enum AncillaryArtifactKind
{
    Transcript,
    Chapters,
    PublishingText,
    EditingInstructions,
    ReviewEvidence,
    UsedSourceRanges,
}

/// <summary>
/// Text supplied by a caller together with the exact origin retained for it.
/// </summary>
public sealed class AncillaryAttributedText
{
    public AncillaryAttributedText(string value, OriginRef origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        Value = AncillaryArtifactContract.RequireText(
            value,
            nameof(value),
            "Ancillary text");
        Origin = origin;
    }

    public string Value { get; }

    public OriginRef Origin { get; }
}

/// <summary>
/// Explicit publishing copy. Null and empty members remain null and empty;
/// the exporter never fills them from project or topic metadata.
/// </summary>
public sealed class AncillaryPublishingText
{
    public AncillaryPublishingText(
        AncillaryAttributedText? title,
        AncillaryAttributedText? summary,
        AncillaryAttributedText? postText,
        IReadOnlyList<AncillaryAttributedText> hashtags)
    {
        ArgumentNullException.ThrowIfNull(hashtags);
        var copiedHashtags = new AncillaryAttributedText[hashtags.Count];
        var values = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copiedHashtags.Length; index++)
        {
            AncillaryAttributedText hashtag = hashtags[index]
                ?? throw new ArgumentException(
                    "Publishing hashtags must not contain null values.",
                    nameof(hashtags));
            if (!values.Add(hashtag.Value))
            {
                throw new ArgumentException(
                    "Publishing hashtags must not contain duplicate values.",
                    nameof(hashtags));
            }

            copiedHashtags[index] = hashtag;
        }

        Title = title;
        Summary = summary;
        PostText = postText;
        Hashtags = Array.AsReadOnly(copiedHashtags);
    }

    public AncillaryAttributedText? Title { get; }

    public AncillaryAttributedText? Summary { get; }

    public AncillaryAttributedText? PostText { get; }

    public IReadOnlyList<AncillaryAttributedText> Hashtags { get; }
}

/// <summary>
/// One explicitly selected chapter and the editorial record that authored its
/// title. Placeholder topic titles are rejected by the exporter.
/// </summary>
public sealed class AncillaryChapter
{
    public AncillaryChapter(
        TopicMetadataRevision metadataRevision,
        OriginRef titleOrigin)
    {
        ArgumentNullException.ThrowIfNull(metadataRevision);
        ArgumentNullException.ThrowIfNull(titleOrigin);
        MetadataRevision = metadataRevision;
        TitleOrigin = titleOrigin;
    }

    public TopicMetadataRevision MetadataRevision { get; }

    public OriginRef TitleOrigin { get; }
}

public sealed class AncillaryArtifactExportRequest
{
    public AncillaryArtifactExportRequest(
        EntityId<AncillaryArtifactExportIdTag> id,
        Project project,
        CompositionVersion compositionVersion,
        TranscriptRevision transcriptRevision,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<AncillaryChapter> chapters,
        AncillaryPublishingText publishingText,
        CompositionVersion? editingSourceVersion,
        StoredVersionEvidence reviewEvidence,
        IReadOnlyList<EntityId<EditorialRecordIdTag>> editorialRecordIds,
        IReadOnlyDictionary<
            AncillaryArtifactKind,
            EntityId<ArtifactIdTag>> artifactIds,
        string language,
        DateTimeOffset createdAt,
        IReadOnlyList<AncillaryArtifactKind>? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(transcriptRevision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(publishingText);
        ArgumentNullException.ThrowIfNull(reviewEvidence);
        ArgumentNullException.ThrowIfNull(editorialRecordIds);
        ArgumentNullException.ThrowIfNull(artifactIds);

        SourceSpan[] copiedSpans = CopyValues(
            sourceSpans,
            nameof(sourceSpans),
            "Source spans");
        AncillaryChapter[] copiedChapters = CopyValues(
            chapters,
            nameof(chapters),
            "Chapters");
        EntityId<EditorialRecordIdTag>[] copiedEditorialIds = CopyUniqueIds(
            editorialRecordIds,
            nameof(editorialRecordIds),
            "Editorial record IDs");
        AncillaryArtifactKind[] copiedKinds = CopyKinds(kinds);
        Dictionary<AncillaryArtifactKind, EntityId<ArtifactIdTag>>
            copiedArtifactIds = CopyArtifactIds(artifactIds, copiedKinds);

        Id = id;
        Project = project;
        CompositionVersion = compositionVersion;
        TranscriptRevision = transcriptRevision;
        SourceSpans = Array.AsReadOnly(copiedSpans);
        Chapters = Array.AsReadOnly(copiedChapters);
        PublishingText = publishingText;
        EditingSourceVersion = editingSourceVersion;
        ReviewEvidence = reviewEvidence;
        EditorialRecordIds = Array.AsReadOnly(copiedEditorialIds);
        Kinds = Array.AsReadOnly(copiedKinds);
        ArtifactIds = new ReadOnlyDictionary<
            AncillaryArtifactKind,
            EntityId<ArtifactIdTag>>(copiedArtifactIds);
        Language = AncillaryArtifactContract.RequireLanguage(
            language,
            nameof(language));
        CreatedAt = createdAt.ToUniversalTime();
    }

    public EntityId<AncillaryArtifactExportIdTag> Id { get; }

    public Project Project { get; }

    public CompositionVersion CompositionVersion { get; }

    public TranscriptRevision TranscriptRevision { get; }

    public IReadOnlyList<SourceSpan> SourceSpans { get; }

    public IReadOnlyList<AncillaryChapter> Chapters { get; }

    public AncillaryPublishingText PublishingText { get; }

    public CompositionVersion? EditingSourceVersion { get; }

    public StoredVersionEvidence ReviewEvidence { get; }

    public IReadOnlyList<EntityId<EditorialRecordIdTag>> EditorialRecordIds
    { get; }

    /// <summary>
    /// The artifact kinds this export writes, in declaration order. Kinds that
    /// are absent are neither staged, promoted, nor registered.
    /// </summary>
    public IReadOnlyList<AncillaryArtifactKind> Kinds { get; }

    public IReadOnlyDictionary<
        AncillaryArtifactKind,
        EntityId<ArtifactIdTag>> ArtifactIds
    { get; }

    public string Language { get; }

    public DateTimeOffset CreatedAt { get; }

    private static T[] CopyValues<T>(
        IReadOnlyList<T> values,
        string parameterName,
        string description)
        where T : class
    {
        var copied = new T[values.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            copied[index] = values[index]
                ?? throw new ArgumentException(
                    string.Concat(
                        description,
                        " must not contain null values."),
                    parameterName);
        }

        return copied;
    }

    private static EntityId<TTag>[] CopyUniqueIds<TTag>(
        IReadOnlyList<EntityId<TTag>> values,
        string parameterName,
        string description)
    {
        var copied = new EntityId<TTag>[values.Count];
        var unique = new HashSet<EntityId<TTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            EntityId<TTag> value = values[index]
                ?? throw new ArgumentException(
                    string.Concat(
                        description,
                        " must not contain null values."),
                    parameterName);
            if (!unique.Add(value))
            {
                throw new ArgumentException(
                    string.Concat(
                        description,
                        " must not contain duplicate values."),
                    parameterName);
            }

            copied[index] = value;
        }

        return copied;
    }

    private static AncillaryArtifactKind[] CopyKinds(
        IReadOnlyList<AncillaryArtifactKind>? kinds)
    {
        AncillaryArtifactKind[] declared =
            Enum.GetValues<AncillaryArtifactKind>();
        if (kinds is null)
        {
            return declared;
        }

        var requested = new HashSet<AncillaryArtifactKind>();
        foreach (AncillaryArtifactKind kind in kinds)
        {
            if (!Enum.IsDefined(kind) || !requested.Add(kind))
            {
                throw new ArgumentException(
                    "Ancillary artifact kinds must be defined and unique.",
                    nameof(kinds));
            }
        }

        if (requested.Count == 0)
        {
            throw new ArgumentException(
                "At least one ancillary artifact kind is required.",
                nameof(kinds));
        }

        return declared.Where(requested.Contains).ToArray();
    }

    private static Dictionary<
        AncillaryArtifactKind,
        EntityId<ArtifactIdTag>> CopyArtifactIds(
        IReadOnlyDictionary<
            AncillaryArtifactKind,
            EntityId<ArtifactIdTag>> artifactIds,
        IReadOnlyList<AncillaryArtifactKind> requiredKinds)
    {
        var copied = new Dictionary<
            AncillaryArtifactKind,
            EntityId<ArtifactIdTag>>();
        var ids = new HashSet<EntityId<ArtifactIdTag>>();
        foreach (KeyValuePair<
            AncillaryArtifactKind,
            EntityId<ArtifactIdTag>> pair in artifactIds)
        {
            if (!Enum.IsDefined(pair.Key) || pair.Value is null)
            {
                throw new ArgumentException(
                    "Ancillary artifact IDs must use defined kinds and non-null IDs.",
                    nameof(artifactIds));
            }

            if (!copied.TryAdd(pair.Key, pair.Value) || !ids.Add(pair.Value))
            {
                throw new ArgumentException(
                    "Ancillary artifact kinds and IDs must be unique.",
                    nameof(artifactIds));
            }
        }

        if (copied.Count != requiredKinds.Count ||
            requiredKinds.Any(kind => !copied.ContainsKey(kind)))
        {
            throw new ArgumentException(
                "Exactly one artifact ID is required for every exported ancillary artifact kind.",
                nameof(artifactIds));
        }

        return copied;
    }
}

public sealed class AncillaryArtifactOutput
{
    internal AncillaryArtifactOutput(
        AncillaryArtifactKind kind,
        EntityId<ArtifactIdTag> artifactId,
        StoredArtifact storedArtifact,
        ManifestArtifact manifestArtifact,
        ContentDigest payloadDigest)
    {
        Kind = kind;
        ArtifactId = artifactId;
        StoredArtifact = storedArtifact;
        ManifestArtifact = manifestArtifact;
        PayloadDigest = payloadDigest;
    }

    public AncillaryArtifactKind Kind { get; }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public StoredArtifact StoredArtifact { get; }

    public ManifestArtifact ManifestArtifact { get; }

    public ContentDigest PayloadDigest { get; }
}

public sealed class AncillaryArtifactExportResult
{
    internal AncillaryArtifactExportResult(
        EntityId<AncillaryArtifactExportIdTag> exportId,
        IReadOnlyList<AncillaryArtifactOutput> artifacts,
        ContentDigest reviewEvidenceDigest,
        bool wasAlreadyRegistered)
    {
        ExportId = exportId;
        Artifacts = Array.AsReadOnly(artifacts.ToArray());
        ReviewEvidenceDigest = reviewEvidenceDigest;
        DeliveryArtifactEvidence = Array.AsReadOnly(
            artifacts
                .Select(static artifact => new KeyValuePair<
                    EntityId<ArtifactIdTag>,
                    ManifestArtifact>(
                        artifact.ArtifactId,
                        artifact.ManifestArtifact))
                .ToArray());
        WasAlreadyRegistered = wasAlreadyRegistered;
    }

    public EntityId<AncillaryArtifactExportIdTag> ExportId { get; }

    public IReadOnlyList<AncillaryArtifactOutput> Artifacts { get; }

    public ContentDigest ReviewEvidenceDigest { get; }

    public IReadOnlyList<KeyValuePair<
        EntityId<ArtifactIdTag>,
        ManifestArtifact>> DeliveryArtifactEvidence
    { get; }

    public bool WasAlreadyRegistered { get; }
}

public enum AncillaryArtifactExportFailureReason
{
    ContextMismatch,
    TranscriptBindingMismatch,
    ChapterEvidenceInvalid,
    PublishingEvidenceInvalid,
    EditingEvidenceInvalid,
    ReviewEvidenceInvalid,
    ProvenanceInvalid,
    SerializationFailed,
    ArtifactConflict,
    StagingConflict,
    StagingUnavailable,
    PromotionFailed,
    RegisteredArtifactInvalid,
    CleanupFailed,
}

public sealed class AncillaryArtifactExportException : Exception
{
    internal AncillaryArtifactExportException(
        AncillaryArtifactExportFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public AncillaryArtifactExportFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(
        AncillaryArtifactExportFailureReason reason) =>
        reason switch
        {
            AncillaryArtifactExportFailureReason.ContextMismatch =>
                "The ancillary export context does not identify one exact project and composition version.",
            AncillaryArtifactExportFailureReason.TranscriptBindingMismatch =>
                "The ancillary transcript evidence is inconsistent.",
            AncillaryArtifactExportFailureReason.ChapterEvidenceInvalid =>
                "The ancillary chapter evidence is not explicitly authored and version-bound.",
            AncillaryArtifactExportFailureReason.PublishingEvidenceInvalid =>
                "The ancillary publishing text contains invalid origin evidence.",
            AncillaryArtifactExportFailureReason.EditingEvidenceInvalid =>
                "The ancillary editing evidence does not identify the exported version transition.",
            AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid =>
                "The ancillary review evidence does not match the exported composition version.",
            AncillaryArtifactExportFailureReason.ProvenanceInvalid =>
                "The ancillary export contains a broken origin reference.",
            AncillaryArtifactExportFailureReason.SerializationFailed =>
                "The ancillary artifacts could not be serialized canonically.",
            AncillaryArtifactExportFailureReason.ArtifactConflict =>
                "The ancillary artifact set conflicts with registered evidence.",
            AncillaryArtifactExportFailureReason.StagingConflict =>
                "The ancillary staging destination is already owned by another operation.",
            AncillaryArtifactExportFailureReason.StagingUnavailable =>
                "The ancillary staging destination is unavailable.",
            AncillaryArtifactExportFailureReason.PromotionFailed =>
                "The ancillary artifacts could not be promoted.",
            AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid =>
                "A registered ancillary artifact could not be validated.",
            AncillaryArtifactExportFailureReason.CleanupFailed =>
                "Ancillary staging cleanup requires reconciliation.",
            _ => "The ancillary artifact export failed.",
        };
}

/// <summary>
/// Exports the six R-024 ancillary JSON artifacts. The service serializes only
/// caller-supplied or deterministic domain evidence; it has no generation or
/// network dependency.
/// </summary>
public sealed class AncillaryArtifactExporter
{
    public const string SchemaVersion = "1.0.0";

    private const int FileBufferSize = 64 * 1024;

    private readonly GetArtifactDelegate _getArtifact;
    private readonly ListArtifactsDelegate _listArtifacts;
    private readonly PromoteDelegate _promoteAsync;
    private readonly ValidateArtifactDelegate _validateArtifactAsync;
    private readonly string _workspacePath;

    public AncillaryArtifactExporter(
        ArtifactStore artifactStore,
        string workspacePath)
        : this(
            GetArtifactDelegateFor(artifactStore),
            GetListArtifactsDelegate(artifactStore),
            GetPromoteDelegate(artifactStore),
            GetValidateArtifactDelegate(artifactStore),
            workspacePath)
    {
    }

    internal AncillaryArtifactExporter(
        GetArtifactDelegate getArtifact,
        ListArtifactsDelegate listArtifacts,
        PromoteDelegate promoteAsync,
        ValidateArtifactDelegate validateArtifactAsync,
        string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(getArtifact);
        ArgumentNullException.ThrowIfNull(listArtifacts);
        ArgumentNullException.ThrowIfNull(promoteAsync);
        ArgumentNullException.ThrowIfNull(validateArtifactAsync);

        _getArtifact = getArtifact;
        _listArtifacts = listArtifacts;
        _promoteAsync = promoteAsync;
        _validateArtifactAsync = validateArtifactAsync;
        _workspacePath = AncillaryArtifactPath.NormalizeExistingWorkspace(
            workspacePath,
            nameof(workspacePath));
    }

    public async Task<AncillaryArtifactExportResult> ExportAsync(
        AncillaryArtifactExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        PreparedExport prepared = Prepare(request, cancellationToken);
        ExistingExportInspection existing = await InspectExistingAsync(
            prepared,
            cancellationToken).ConfigureAwait(false);
        if (existing.State == ExistingExportState.Exact)
        {
            return BuildResult(
                prepared,
                existing.Artifacts,
                wasAlreadyRegistered: true);
        }

        if (existing.State == ExistingExportState.Conflict)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.ArtifactConflict,
                reconciliationRequired: true);
        }

        ExportStaging staging = PrepareStaging(prepared, cancellationToken);
        try
        {
            await WriteStagingAsync(
                staging,
                prepared.Artifacts,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ThrowIfCleanupFailed(staging);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AncillaryArtifactExportException)
        {
            ThrowIfCleanupFailed(staging);
            throw;
        }
        catch (Exception)
        {
            ThrowIfCleanupFailed(staging);
            throw Failure(
                AncillaryArtifactExportFailureReason.StagingUnavailable);
        }

        var promoted = new List<StoredArtifact>(prepared.Artifacts.Count);
        for (int index = 0; index < prepared.Artifacts.Count; index++)
        {
            PreparedArtifact artifact = prepared.Artifacts[index];
            CancellationToken promotionToken = index == 0
                ? cancellationToken
                : CancellationToken.None;
            StoredArtifact stored;
            try
            {
                stored = await _promoteAsync(
                    new ArtifactPromotionRequest(
                        artifact.ArtifactId,
                        prepared.Request.Project.Id,
                        ArtifactKind.Delivery,
                        staging.Paths[artifact.Kind],
                        artifact.RelativePath,
                        artifact.ArtifactDigest,
                        artifact.CanonicalBytes.LongLength,
                        prepared.Request.CreatedAt),
                    promotionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (index == 0 && cancellationToken.IsCancellationRequested)
            {
                return await ResolveFailedPromotionAsync(
                    prepared,
                    staging,
                    durableArtifactCount: 0,
                    preserveCancellation: true,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArtifactStoreException exception)
            {
                bool uncertain = exception.ReconciliationRequired ||
                    exception.Reason is ArtifactStoreFailureReason.DestinationExists
                        or ArtifactStoreFailureReason.DatabaseRejected;
                return await ResolveFailedPromotionAsync(
                    prepared,
                    staging,
                    promoted.Count,
                    preserveCancellation: false,
                    cancellationToken,
                    uncertain).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await ResolveFailedPromotionAsync(
                    prepared,
                    staging,
                    promoted.Count,
                    preserveCancellation: false,
                    cancellationToken,
                    uncertainFailure: true).ConfigureAwait(false);
            }

            if (!MatchesExpected(artifact, prepared.Request, stored))
            {
                ThrowIfCleanupFailed(staging);
                throw Failure(
                    AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                    reconciliationRequired: true);
            }

            promoted.Add(stored);
        }

        IReadOnlyList<StoredArtifact> verified;
        try
        {
            verified = await ValidateRegisteredSetAsync(
                prepared,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (AncillaryArtifactExportException)
        {
            ThrowIfCleanupFailed(staging);
            throw;
        }
        catch (Exception)
        {
            ThrowIfCleanupFailed(staging);
            throw Failure(
                AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                reconciliationRequired: true);
        }

        if (!TryCleanupOwnedStaging(staging))
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.CleanupFailed,
                reconciliationRequired: true);
        }

        return BuildResult(
            prepared,
            verified,
            wasAlreadyRegistered: false);
    }

    private static PreparedExport Prepare(
        AncillaryArtifactExportRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateComposition(request.Project, request.CompositionVersion);
        CompositionDiff? editingDiff = ValidateEditingEvidence(request);
        cancellationToken.ThrowIfCancellationRequested();

        ValidatedTranscript transcript = ValidateTranscriptEvidence(request);
        ValidatedReview review = ValidateReviewEvidence(request);
        ValidatedChapter[] chapters = ValidateChapters(
            request,
            transcript.Digest);
        AncillaryAttributedText[] hashtags = ValidatePublishingText(request);
        ValidationCatalog catalog = ValidateProvenance(
            request,
            transcript,
            chapters,
            hashtags,
            editingDiff,
            review);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            byte[] reviewPayload = WriteReviewPayload(review);
            ContentDigest reviewEvidenceDigest = ContentDigest.Compute(
                reviewPayload);
            var artifacts = new List<PreparedArtifact>(request.Kinds.Count);
            foreach (AncillaryArtifactKind kind in request.Kinds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] payload = kind switch
                {
                    AncillaryArtifactKind.Transcript =>
                        WriteTranscriptPayload(transcript),
                    AncillaryArtifactKind.Chapters => WriteChaptersPayload(
                        request.TranscriptRevision.Id,
                        transcript.Digest,
                        chapters),
                    AncillaryArtifactKind.PublishingText =>
                        WritePublishingPayload(request.PublishingText, hashtags),
                    AncillaryArtifactKind.EditingInstructions =>
                        WriteEditingPayload(
                            request.CompositionVersion,
                            request.EditingSourceVersion,
                            editingDiff),
                    AncillaryArtifactKind.ReviewEvidence => reviewPayload,
                    AncillaryArtifactKind.UsedSourceRanges =>
                        WriteUsedSourceRangesPayload(
                            request.CompositionVersion,
                            catalog.SourceSpans),
                    _ => throw Failure(
                        AncillaryArtifactExportFailureReason.SerializationFailed),
                };
                ContentDigest payloadDigest = ContentDigest.Compute(payload);
                byte[] envelope = WriteEnvelope(
                    request,
                    kind,
                    reviewEvidenceDigest,
                    payloadDigest,
                    catalog.Origins,
                    payload);
                ContentDigest artifactDigest = ContentDigest.Compute(envelope);
                string relativePath = BuildRelativePath(request, kind);
                artifacts.Add(new PreparedArtifact(
                    kind,
                    request.ArtifactIds[kind],
                    payloadDigest,
                    artifactDigest,
                    envelope,
                    relativePath,
                    BuildStorageReference(request.Project.Id, relativePath)));
            }

            return new PreparedExport(
                request,
                Array.AsReadOnly(artifacts.ToArray()),
                reviewEvidenceDigest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AncillaryArtifactExportException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException or EncoderFallbackException or
                JsonException or InvalidOperationException or OverflowException)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.SerializationFailed);
        }
    }

    private static void ValidateComposition(
        Project project,
        CompositionVersion version)
    {
        if (version.ProjectId != project.Id)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.ContextMismatch);
        }

        try
        {
            if (RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    version.Elements) != version.ContentDigest)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.ContextMismatch);
            }
        }
        catch (AncillaryArtifactExportException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.ContextMismatch);
        }
    }

    private static CompositionDiff? ValidateEditingEvidence(
        AncillaryArtifactExportRequest request)
    {
        CompositionVersion target = request.CompositionVersion;
        CompositionVersion? source = request.EditingSourceVersion;
        if (target.ParentVersionId is null)
        {
            if (source is not null)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.EditingEvidenceInvalid);
            }

            return null;
        }

        if (source is null ||
            source.Id != target.ParentVersionId ||
            source.ProjectId != request.Project.Id)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.EditingEvidenceInvalid);
        }

        try
        {
            if (RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    source.Elements) != source.ContentDigest)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.EditingEvidenceInvalid);
            }

            return VersionService.Compare(source, target);
        }
        catch (AncillaryArtifactExportException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.EditingEvidenceInvalid);
        }
    }

    private static ValidatedTranscript ValidateTranscriptEvidence(
        AncillaryArtifactExportRequest request)
    {
        byte[] canonicalBytes;
        ContentDigest digest;
        try
        {
            string canonical = TranscriptSnapshotJson.WriteRevision(
                request.TranscriptRevision);
            canonicalBytes = AncillaryArtifactContract.StrictUtf8.GetBytes(
                canonical);
            digest = ContentDigest.Compute(canonicalBytes);
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.TranscriptBindingMismatch);
        }

        var assets = request.Project.Assets.ToDictionary(static asset => asset.Id);
        var spans = new Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>();
        foreach (SourceSpan span in request.SourceSpans)
        {
            if (!spans.TryAdd(span.Id, span) ||
                !assets.TryGetValue(span.AssetId, out Asset? asset) ||
                asset.ContentDigest != span.SourceDigest ||
                span.TranscriptRevisionId is not null &&
                    span.TranscriptRevisionId != request.TranscriptRevision.Id)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.TranscriptBindingMismatch);
            }
        }

        var transcriptSpans = new SourceSpan[
            request.TranscriptRevision.Utterances.Count];
        var usedIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < transcriptSpans.Length; index++)
        {
            Utterance utterance = request.TranscriptRevision.Utterances[index];
            if (!spans.TryGetValue(utterance.SourceSpanId, out SourceSpan? span) ||
                span.TranscriptRevisionId != request.TranscriptRevision.Id ||
                span.Range != utterance.Range ||
                !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)) ||
                !usedIds.Add(span.Id))
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.TranscriptBindingMismatch);
            }

            transcriptSpans[index] = span;
        }

        Dictionary<EntityId<TranscriptTokenIdTag>, TimeRange> tokenRanges =
            BuildTokenRanges(request.TranscriptRevision);
        if (request.SourceSpans.Any(span =>
                span.TranscriptRevisionId == request.TranscriptRevision.Id &&
                !usedIds.Contains(span.Id) &&
                !IsTokenGroundedSpan(span, tokenRanges)))
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.TranscriptBindingMismatch);
        }

        return new ValidatedTranscript(
            canonicalBytes,
            digest,
            Array.AsReadOnly(transcriptSpans),
            new ReadOnlyDictionary<
                EntityId<SourceSpanIdTag>,
                SourceSpan>(spans));
    }

    /// <summary>
    /// A span that is bound to the revision without being one of its utterance
    /// spans (a source envelope or a derived candidate range) is accepted only
    /// when every token it claims is a real token of that revision and the time
    /// range that token is grounded in lies inside the span. This still rejects
    /// a span that invents transcript provenance.
    /// </summary>
    private static bool IsTokenGroundedSpan(
        SourceSpan span,
        IReadOnlyDictionary<EntityId<TranscriptTokenIdTag>, TimeRange> tokenRanges)
    {
        if (span.TranscriptTokenIds.Count == 0)
        {
            return false;
        }

        foreach (EntityId<TranscriptTokenIdTag> tokenId in span.TranscriptTokenIds)
        {
            if (!tokenRanges.TryGetValue(tokenId, out TimeRange grounded) ||
                grounded.Start < span.Range.Start ||
                grounded.End > span.Range.End)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The exact range each token is grounded in: its own measured range when
    /// the recognizer supplied one, otherwise its utterance's range. This is
    /// the same fallback the candidate producers use when building a span.
    /// </summary>
    private static Dictionary<EntityId<TranscriptTokenIdTag>, TimeRange> BuildTokenRanges(
        TranscriptRevision revision)
    {
        var ranges = new Dictionary<EntityId<TranscriptTokenIdTag>, TimeRange>();
        foreach (Utterance utterance in revision.Utterances)
        {
            foreach (Token token in utterance.Tokens)
            {
                ranges[token.Id] = token.Range ?? utterance.Range;
            }
        }

        return ranges;
    }

    private static ValidatedReview ValidateReviewEvidence(
        AncillaryArtifactExportRequest request)
    {
        StoredVersionEvidence evidence = request.ReviewEvidence;
        CompositionVersion version = request.CompositionVersion;
        if (evidence.ProjectId != request.Project.Id ||
            evidence.CompositionVersionId != version.Id ||
            evidence.CompositionVersionDigest != version.ContentDigest)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
        }

        try
        {
            StoredReviewRecord[] records = evidence.ReviewRecords.ToArray();
            StoredReviewComment[] comments = evidence.ReviewComments.ToArray();
            StoredApproval[] approvals = evidence.Approvals.ToArray();
            var recordIds = new HashSet<EntityId<ReviewRecordIdTag>>();
            var commentIds = new HashSet<EntityId<ReviewCommentIdTag>>();
            var approvalIds = new HashSet<EntityId<ApprovalIdTag>>();

            foreach (StoredReviewRecord record in records)
            {
                if (record is null ||
                    !recordIds.Add(record.Id) ||
                    !MatchesReviewBinding(
                        record.ProjectId,
                        record.CompositionVersionId,
                        record.CompositionVersionDigest,
                        request) ||
                    !Enum.IsDefined(record.State) ||
                    !Enum.IsDefined(record.ActorRole))
                {
                    throw Failure(
                        AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
                }

                _ = AncillaryArtifactContract.RequireText(
                    record.ActorId,
                    nameof(record.ActorId),
                    "Review actor ID");
                _ = AncillaryArtifactContract.RequireText(
                    record.Reason,
                    nameof(record.Reason),
                    "Review reason");
                _ = record.RecordedAt.ToUniversalTime();
            }

            foreach (StoredReviewComment comment in comments)
            {
                if (comment is null ||
                    !commentIds.Add(comment.Id) ||
                    !MatchesReviewBinding(
                        comment.ProjectId,
                        comment.CompositionVersionId,
                        comment.CompositionVersionDigest,
                        request) ||
                    !Enum.IsDefined(comment.ActorRole) ||
                    comment.OutputTime < default(RationalTime))
                {
                    throw Failure(
                        AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
                }

                _ = AncillaryArtifactContract.RequireText(
                    comment.ActorId,
                    nameof(comment.ActorId),
                    "Review-comment actor ID");
                _ = AncillaryArtifactContract.RequireText(
                    comment.Text,
                    nameof(comment.Text),
                    "Review-comment text");
                _ = comment.CreatedAt.ToUniversalTime();
            }

            foreach (StoredApproval approval in approvals)
            {
                if (approval is null ||
                    !approvalIds.Add(approval.Id) ||
                    !MatchesReviewBinding(
                        approval.ProjectId,
                        approval.CompositionVersionId,
                        approval.CompositionVersionDigest,
                        request) ||
                    approval.ActorRole is not (
                        ReviewActorRole.ContentApprover or
                        ReviewActorRole.BrandApprover or
                        ReviewActorRole.RightsApprover))
                {
                    throw Failure(
                        AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
                }

                _ = AncillaryArtifactContract.RequireText(
                    approval.ActorId,
                    nameof(approval.ActorId),
                    "Approval actor ID");
                _ = AncillaryArtifactContract.RequireText(
                    approval.Reason,
                    nameof(approval.Reason),
                    "Approval reason");
                _ = approval.ApprovedAt.ToUniversalTime();
            }

            return new ValidatedReview(
                Array.AsReadOnly(records
                    .OrderBy(static record => record.RecordedAt)
                    .ThenBy(
                        static record => record.Id.ToString(),
                        StringComparer.Ordinal)
                    .ToArray()),
                Array.AsReadOnly(comments
                    .OrderBy(static comment => comment.CreatedAt)
                    .ThenBy(
                        static comment => comment.Id.ToString(),
                        StringComparer.Ordinal)
                    .ToArray()),
                Array.AsReadOnly(approvals
                    .OrderBy(static approval => approval.ApprovedAt)
                    .ThenBy(
                        static approval => approval.Id.ToString(),
                        StringComparer.Ordinal)
                    .ToArray()),
                request.Project.Id,
                version.Id,
                version.ContentDigest);
        }
        catch (AncillaryArtifactExportException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
        }
    }

    private static bool MatchesReviewBinding(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> versionId,
        ContentDigest versionDigest,
        AncillaryArtifactExportRequest request) =>
        projectId == request.Project.Id &&
        versionId == request.CompositionVersion.Id &&
        versionDigest == request.CompositionVersion.ContentDigest;

    private static ValidatedChapter[] ValidateChapters(
        AncillaryArtifactExportRequest request,
        ContentDigest transcriptDigest)
    {
        try
        {
            var topicIds = new HashSet<EntityId<TopicMetadataIdTag>>();
            var revisionIds = new HashSet<EntityId<TopicMetadataRevisionIdTag>>();
            var chapters = new ValidatedChapter[request.Chapters.Count];
            RationalTime? transcriptStart = request.TranscriptRevision.Utterances.Count == 0
                ? null
                : request.TranscriptRevision.Utterances[0].Range.Start;
            RationalTime? transcriptEnd = request.TranscriptRevision.Utterances.Count == 0
                ? null
                : request.TranscriptRevision.Utterances.Max(
                    static utterance => utterance.Range.End);

            for (int index = 0; index < chapters.Length; index++)
            {
                AncillaryChapter chapter = request.Chapters[index];
                TopicMetadataRevision revision = chapter.MetadataRevision;
                if (revision.ProjectId != request.Project.Id ||
                    revision.SourceRevisionId != request.TranscriptRevision.Id ||
                    revision.SourceRevisionDigest != transcriptDigest ||
                    revision.Metadata.TitleOrigin !=
                        TopicMetadataValueOrigin.ManualEditorial ||
                    revision.Metadata.TitleRequiresManualReview ||
                    chapter.TitleOrigin.Kind != OriginKind.EditorialRecord ||
                    chapter.TitleOrigin.EditorialRecordId is null ||
                    !request.EditorialRecordIds.Contains(
                        chapter.TitleOrigin.EditorialRecordId) ||
                    !topicIds.Add(revision.TopicMetadataId) ||
                    !revisionIds.Add(revision.Id) ||
                    transcriptStart is not RationalTime start ||
                    transcriptEnd is not RationalTime end ||
                    revision.TopicRange.Start < start ||
                    revision.TopicRange.End > end)
                {
                    throw Failure(
                        AncillaryArtifactExportFailureReason.ChapterEvidenceInvalid);
                }

                _ = AncillaryArtifactContract.RequireText(
                    revision.Metadata.Title,
                    nameof(revision.Metadata.Title),
                    "Chapter title");
                chapters[index] = new ValidatedChapter(
                    revision,
                    chapter.TitleOrigin);
            }

            ValidatedChapter[] ordered = chapters
                .OrderBy(static chapter => chapter.Revision.TopicRange.Start)
                .ThenBy(
                    static chapter => chapter.Revision.TopicRange.End)
                .ThenBy(
                    static chapter => chapter.Revision.Id.ToString(),
                    StringComparer.Ordinal)
                .ToArray();
            for (int index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].Revision.TopicRange.Start <
                    ordered[index - 1].Revision.TopicRange.End)
                {
                    throw Failure(
                        AncillaryArtifactExportFailureReason.ChapterEvidenceInvalid);
                }
            }

            return ordered;
        }
        catch (AncillaryArtifactExportException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.ChapterEvidenceInvalid);
        }
    }

    private static AncillaryAttributedText[] ValidatePublishingText(
        AncillaryArtifactExportRequest request)
    {
        try
        {
            AncillaryAttributedText[] hashtags = request.PublishingText.Hashtags
                .OrderBy(static hashtag => hashtag.Value, StringComparer.Ordinal)
                .ThenBy(static hashtag => GetOriginIdentity(hashtag.Origin), StringComparer.Ordinal)
                .ToArray();
            if (hashtags.Select(static hashtag => hashtag.Value)
                .Distinct(StringComparer.Ordinal)
                .Count() != hashtags.Length)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.PublishingEvidenceInvalid);
            }

            return hashtags;
        }
        catch (AncillaryArtifactExportException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.PublishingEvidenceInvalid);
        }
    }

    private static ValidationCatalog ValidateProvenance(
        AncillaryArtifactExportRequest request,
        ValidatedTranscript transcript,
        IReadOnlyList<ValidatedChapter> chapters,
        IReadOnlyList<AncillaryAttributedText> hashtags,
        CompositionDiff? editingDiff,
        ValidatedReview review)
    {
        var assets = request.Project.Assets.ToDictionary(static asset => asset.Id);
        var editorialIds = request.EditorialRecordIds.ToHashSet();
        var origins = new Dictionary<string, OriginRef>(StringComparer.Ordinal);

        void ValidateAndAdd(OriginRef origin)
        {
            bool valid = origin.Kind switch
            {
                OriginKind.SourceSpan when origin.SourceSpanId is not null =>
                    transcript.AllSourceSpans.ContainsKey(origin.SourceSpanId),
                OriginKind.Asset when origin.AssetId is not null =>
                    assets.ContainsKey(origin.AssetId),
                OriginKind.EditorialRecord
                    when origin.EditorialRecordId is not null =>
                    editorialIds.Contains(origin.EditorialRecordId),
                _ => false,
            };
            if (!valid)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.ProvenanceInvalid);
            }

            origins.TryAdd(GetOriginIdentity(origin), origin);
        }

        foreach (SourceSpan span in request.SourceSpans)
        {
            if (!assets.TryGetValue(span.AssetId, out Asset? asset) ||
                asset.ContentDigest != span.SourceDigest)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.ProvenanceInvalid);
            }
        }

        ValidateCompositionOrigins(
            request.CompositionVersion.Elements,
            transcript.AllSourceSpans,
            ValidateAndAdd);
        if (request.EditingSourceVersion is not null)
        {
            ValidateCompositionOrigins(
                request.EditingSourceVersion.Elements,
                transcript.AllSourceSpans,
                ValidateAndAdd);
        }

        foreach (SourceSpan span in transcript.TranscriptSourceSpans)
        {
            ValidateAndAdd(OriginRef.FromSourceSpan(span.Id));
        }

        foreach (ValidatedChapter chapter in chapters)
        {
            ValidateAndAdd(chapter.TitleOrigin);
        }

        AddPublishingOrigin(request.PublishingText.Title, ValidateAndAdd);
        AddPublishingOrigin(request.PublishingText.Summary, ValidateAndAdd);
        AddPublishingOrigin(request.PublishingText.PostText, ValidateAndAdd);
        foreach (AncillaryAttributedText hashtag in hashtags)
        {
            ValidateAndAdd(hashtag.Origin);
        }

        foreach (StoredReviewComment comment in review.Comments)
        {
            if (!transcript.AllSourceSpans.ContainsKey(comment.SourceSpanId) ||
                !request.CompositionVersion.Elements.Any(element =>
                    element.Origin.Kind == OriginKind.SourceSpan &&
                    element.Origin.SourceSpanId == comment.SourceSpanId &&
                    element.OutputRange.Contains(comment.OutputTime)))
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid);
            }

            ValidateAndAdd(OriginRef.FromSourceSpan(comment.SourceSpanId));
        }

        if (editingDiff is not null)
        {
            foreach (CompositionChange change in editingDiff.Changes)
            {
                if (change.PreviousElement is not null)
                {
                    ValidateAndAdd(change.PreviousElement.Origin);
                }

                if (change.CurrentElement is not null)
                {
                    ValidateAndAdd(change.CurrentElement.Origin);
                }
            }
        }

        if (origins.Count == 0)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.ProvenanceInvalid);
        }

        OriginRef[] orderedOrigins = origins.Values
            .OrderBy(static origin => (int)origin.Kind)
            .ThenBy(GetOriginIdentity, StringComparer.Ordinal)
            .ToArray();
        return new ValidationCatalog(
            transcript.AllSourceSpans,
            Array.AsReadOnly(orderedOrigins));
    }

    private static void ValidateCompositionOrigins(
        IReadOnlyList<CompositionElement> elements,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceSpans,
        Action<OriginRef> validateAndAdd)
    {
        foreach (CompositionElement element in elements)
        {
            validateAndAdd(element.Origin);
            if (element.Origin.Kind == OriginKind.SourceSpan)
            {
                if (element.Origin.SourceSpanId is not
                        EntityId<SourceSpanIdTag> sourceSpanId ||
                    !sourceSpans.TryGetValue(sourceSpanId, out SourceSpan? span) ||
                    element.SourceRange is not TimeRange sourceRange ||
                    sourceRange.Start < span.Range.Start ||
                    sourceRange.End > span.Range.End)
                {
                    throw Failure(
                        AncillaryArtifactExportFailureReason.ProvenanceInvalid);
                }
            }
            else if (element.SourceRange is not null)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.ProvenanceInvalid);
            }

            if (element.AdoptedSuggestion is not null &&
                element.AdoptedSuggestion.Origin != element.Origin)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.ProvenanceInvalid);
            }
        }
    }

    private static void AddPublishingOrigin(
        AncillaryAttributedText? value,
        Action<OriginRef> validateAndAdd)
    {
        if (value is not null)
        {
            validateAndAdd(value.Origin);
        }
    }

    private static byte[] WriteTranscriptPayload(ValidatedTranscript transcript) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(
                writer,
                "transcriptRevisionDigest",
                transcript.Digest.Hex);
            writer.WritePropertyName("revision");
            using (JsonDocument revision = JsonDocument.Parse(
                transcript.CanonicalRevisionBytes))
            {
                revision.RootElement.WriteTo(writer);
            }

            writer.WritePropertyName("sourceSpans");
            writer.WriteStartArray();
            foreach (SourceSpan span in transcript.TranscriptSourceSpans)
            {
                WriteSourceSpan(writer, span);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static byte[] WriteChaptersPayload(
        EntityId<TranscriptRevisionIdTag> transcriptRevisionId,
        ContentDigest transcriptRevisionDigest,
        IReadOnlyList<ValidatedChapter> chapters) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(
                writer,
                "transcriptRevisionId",
                transcriptRevisionId.ToString());
            WriteString(
                writer,
                "transcriptRevisionDigest",
                transcriptRevisionDigest.Hex);
            writer.WritePropertyName("chapters");
            writer.WriteStartArray();
            foreach (ValidatedChapter chapter in chapters)
            {
                TopicMetadataRevision revision = chapter.Revision;
                writer.WriteStartObject();
                WriteString(
                    writer,
                    "topicMetadataId",
                    revision.TopicMetadataId.ToString());
                WriteString(
                    writer,
                    "topicMetadataRevisionId",
                    revision.Id.ToString());
                WriteString(
                    writer,
                    "topicRangeId",
                    revision.TopicRangeId.ToString());
                WriteString(
                    writer,
                    "topicRangeRevisionId",
                    revision.TopicRangeRevisionId.ToString());
                writer.WritePropertyName("range");
                WriteTimeRange(writer, revision.TopicRange);
                WriteString(writer, "title", revision.Metadata.Title);
                WriteString(writer, "titleValueOrigin", "manualEditorial");
                writer.WritePropertyName("origin");
                WriteOrigin(writer, chapter.TitleOrigin);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static byte[] WritePublishingPayload(
        AncillaryPublishingText publishing,
        IReadOnlyList<AncillaryAttributedText> hashtags) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteNullableAttributedText(writer, "title", publishing.Title);
            WriteNullableAttributedText(writer, "summary", publishing.Summary);
            WriteNullableAttributedText(writer, "postText", publishing.PostText);
            writer.WritePropertyName("hashtags");
            writer.WriteStartArray();
            foreach (AncillaryAttributedText hashtag in hashtags)
            {
                WriteAttributedText(writer, hashtag);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static byte[] WriteEditingPayload(
        CompositionVersion target,
        CompositionVersion? source,
        CompositionDiff? diff) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            if (source is null)
            {
                writer.WriteNull("fromVersionId");
                writer.WriteNull("fromVersionDigest");
            }
            else
            {
                WriteString(writer, "fromVersionId", source.Id.ToString());
                WriteString(
                    writer,
                    "fromVersionDigest",
                    source.ContentDigest.Hex);
            }

            WriteString(writer, "toVersionId", target.Id.ToString());
            WriteString(
                writer,
                "toVersionDigest",
                target.ContentDigest.Hex);
            writer.WritePropertyName("changes");
            writer.WriteStartArray();
            foreach (CompositionChange change in
                diff?.Changes ?? [])
            {
                writer.WriteStartObject();
                WriteString(
                    writer,
                    "kind",
                    GetCompositionChangeKind(change.Kind));
                WriteString(writer, "elementId", change.ElementId.ToString());
                WriteNullableInt32(writer, "previousIndex", change.PreviousIndex);
                WriteNullableInt32(writer, "currentIndex", change.CurrentIndex);
                writer.WritePropertyName("previousElement");
                WriteNullableElement(writer, change.PreviousElement);
                writer.WritePropertyName("currentElement");
                WriteNullableElement(writer, change.CurrentElement);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static byte[] WriteReviewPayload(ValidatedReview review) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(writer, "projectId", review.ProjectId.ToString());
            WriteString(
                writer,
                "compositionVersionId",
                review.CompositionVersionId.ToString());
            WriteString(
                writer,
                "compositionVersionDigest",
                review.CompositionVersionDigest.Hex);
            writer.WritePropertyName("reviewRecords");
            writer.WriteStartArray();
            foreach (StoredReviewRecord record in review.Records)
            {
                writer.WriteStartObject();
                WriteString(writer, "id", record.Id.ToString());
                WriteString(writer, "state", GetReviewState(record.State));
                WriteString(writer, "actorId", record.ActorId);
                WriteString(writer, "actorRole", GetReviewActorRole(record.ActorRole));
                WriteTimestamp(writer, "recordedAt", record.RecordedAt);
                WriteString(writer, "reason", record.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("reviewComments");
            writer.WriteStartArray();
            foreach (StoredReviewComment comment in review.Comments)
            {
                writer.WriteStartObject();
                WriteString(writer, "id", comment.Id.ToString());
                writer.WritePropertyName("outputTime");
                WriteRationalTime(writer, comment.OutputTime);
                WriteString(
                    writer,
                    "sourceSpanId",
                    comment.SourceSpanId.ToString());
                WriteString(writer, "actorId", comment.ActorId);
                WriteString(writer, "actorRole", GetReviewActorRole(comment.ActorRole));
                WriteTimestamp(writer, "createdAt", comment.CreatedAt);
                WriteString(writer, "text", comment.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("approvals");
            writer.WriteStartArray();
            foreach (StoredApproval approval in review.Approvals)
            {
                writer.WriteStartObject();
                WriteString(writer, "id", approval.Id.ToString());
                WriteString(writer, "actorId", approval.ActorId);
                WriteString(writer, "actorRole", GetReviewActorRole(approval.ActorRole));
                WriteTimestamp(writer, "approvedAt", approval.ApprovedAt);
                WriteString(writer, "reason", approval.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static byte[] WriteUsedSourceRangesPayload(
        CompositionVersion version,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceSpans) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (CompositionElement element in version.Elements)
            {
                if (element.Origin.Kind != OriginKind.SourceSpan ||
                    element.Origin.SourceSpanId is not
                        EntityId<SourceSpanIdTag> sourceSpanId ||
                    element.SourceRange is not TimeRange sourceRange)
                {
                    continue;
                }

                SourceSpan sourceSpan = sourceSpans[sourceSpanId];
                writer.WriteStartObject();
                WriteString(
                    writer,
                    "compositionElementId",
                    element.Id.ToString());
                writer.WritePropertyName("outputRange");
                WriteTimeRange(writer, element.OutputRange);
                writer.WritePropertyName("origin");
                WriteOrigin(writer, element.Origin);
                writer.WritePropertyName("sourceRange");
                WriteTimeRange(writer, sourceRange);
                writer.WritePropertyName("sourceSpan");
                WriteSourceSpan(writer, sourceSpan);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static byte[] WriteEnvelope(
        AncillaryArtifactExportRequest request,
        AncillaryArtifactKind kind,
        ContentDigest reviewEvidenceDigest,
        ContentDigest payloadDigest,
        IReadOnlyList<OriginRef> origins,
        byte[] payload) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(writer, "schemaVersion", SchemaVersion);
            WriteString(writer, "exportId", request.Id.ToString());
            WriteString(writer, "kind", GetKindName(kind));
            WriteString(writer, "projectId", request.Project.Id.ToString());
            WriteString(
                writer,
                "compositionVersionId",
                request.CompositionVersion.Id.ToString());
            WriteString(
                writer,
                "compositionVersionDigest",
                request.CompositionVersion.ContentDigest.Hex);
            WriteString(
                writer,
                "reviewEvidenceDigest",
                reviewEvidenceDigest.Hex);
            WriteString(writer, "language", request.Language);
            WriteString(writer, "payloadDigest", payloadDigest.Hex);
            writer.WritePropertyName("origins");
            writer.WriteStartArray();
            foreach (OriginRef origin in origins)
            {
                WriteOrigin(writer, origin);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("payload");
            using (JsonDocument payloadDocument = JsonDocument.Parse(payload))
            {
                payloadDocument.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
        });

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

    private static void WriteNullableAttributedText(
        Utf8JsonWriter writer,
        string propertyName,
        AncillaryAttributedText? value)
    {
        writer.WritePropertyName(propertyName);
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteAttributedText(writer, value);
        }
    }

    private static void WriteAttributedText(
        Utf8JsonWriter writer,
        AncillaryAttributedText value)
    {
        writer.WriteStartObject();
        WriteString(writer, "value", value.Value);
        writer.WritePropertyName("origin");
        WriteOrigin(writer, value.Origin);
        writer.WriteEndObject();
    }

    private static void WriteNullableElement(
        Utf8JsonWriter writer,
        CompositionElement? element)
    {
        if (element is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteString(writer, "id", element.Id.ToString());
        writer.WritePropertyName("outputRange");
        WriteTimeRange(writer, element.OutputRange);
        writer.WritePropertyName("origin");
        WriteOrigin(writer, element.Origin);
        if (element.SourceRange is TimeRange sourceRange)
        {
            writer.WritePropertyName("sourceRange");
            WriteTimeRange(writer, sourceRange);
        }

        if (element.AdoptedSuggestion is not null)
        {
            writer.WritePropertyName("adoptedSuggestion");
            writer.WriteStartObject();
            WriteString(
                writer,
                "suggestionId",
                element.AdoptedSuggestion.SuggestionId.ToString());
            writer.WritePropertyName("origin");
            WriteOrigin(writer, element.AdoptedSuggestion.Origin);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteSourceSpan(
        Utf8JsonWriter writer,
        SourceSpan span)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", span.Id.ToString());
        WriteString(writer, "assetId", span.AssetId.ToString());
        WriteString(writer, "streamId", span.StreamId);
        writer.WritePropertyName("range");
        WriteTimeRange(writer, span.Range);
        WriteString(writer, "sourceDigest", span.SourceDigest.Hex);
        if (span.TranscriptRevisionId is null)
        {
            writer.WriteNull("transcriptRevisionId");
        }
        else
        {
            WriteString(
                writer,
                "transcriptRevisionId",
                span.TranscriptRevisionId.ToString());
        }

        writer.WritePropertyName("transcriptTokenIds");
        writer.WriteStartArray();
        foreach (EntityId<TranscriptTokenIdTag> tokenId in
            span.TranscriptTokenIds)
        {
            WriteStringValue(writer, tokenId.ToString());
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        writer.WriteStartObject();
        switch (origin.Kind)
        {
            case OriginKind.SourceSpan when origin.SourceSpanId is not null:
                WriteString(writer, "kind", "sourceSpan");
                WriteString(
                    writer,
                    "sourceSpanId",
                    origin.SourceSpanId.ToString());
                break;
            case OriginKind.Asset when origin.AssetId is not null:
                WriteString(writer, "kind", "asset");
                WriteString(writer, "assetId", origin.AssetId.ToString());
                break;
            case OriginKind.EditorialRecord
                when origin.EditorialRecordId is not null:
                WriteString(writer, "kind", "editorialRecord");
                WriteString(
                    writer,
                    "editorialRecordId",
                    origin.EditorialRecordId.ToString());
                break;
            default:
                throw Failure(
                    AncillaryArtifactExportFailureReason.ProvenanceInvalid);
        }

        writer.WriteEndObject();
    }

    private static void WriteTimeRange(
        Utf8JsonWriter writer,
        TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteRationalTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteRationalTime(writer, range.Duration);
        writer.WriteEndObject();
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

    private static void WriteNullableInt32(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value) =>
        WriteString(
            writer,
            propertyName,
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static void WriteString(
        Utf8JsonWriter writer,
        string propertyName,
        string value)
    {
        _ = AncillaryArtifactContract.StrictUtf8.GetByteCount(value);
        writer.WriteString(propertyName, value);
    }

    private static void WriteStringValue(
        Utf8JsonWriter writer,
        string value)
    {
        _ = AncillaryArtifactContract.StrictUtf8.GetByteCount(value);
        writer.WriteStringValue(value);
    }

    private static string GetKindName(AncillaryArtifactKind kind) =>
        kind switch
        {
            AncillaryArtifactKind.Transcript => "transcript",
            AncillaryArtifactKind.Chapters => "chapters",
            AncillaryArtifactKind.PublishingText => "publishingText",
            AncillaryArtifactKind.EditingInstructions => "editingInstructions",
            AncillaryArtifactKind.ReviewEvidence => "reviewEvidence",
            AncillaryArtifactKind.UsedSourceRanges => "usedSourceRanges",
            _ => throw Failure(
                AncillaryArtifactExportFailureReason.SerializationFailed),
        };

    private static string GetArtifactType(AncillaryArtifactKind kind) =>
        kind switch
        {
            AncillaryArtifactKind.Transcript => "transcript",
            AncillaryArtifactKind.Chapters => "chapters",
            AncillaryArtifactKind.PublishingText => "publishing-text",
            AncillaryArtifactKind.EditingInstructions => "editing-instructions",
            AncillaryArtifactKind.ReviewEvidence => "review-evidence",
            AncillaryArtifactKind.UsedSourceRanges => "used-source-ranges",
            _ => throw Failure(
                AncillaryArtifactExportFailureReason.SerializationFailed),
        };

    private static string GetFileName(AncillaryArtifactKind kind) =>
        string.Concat(GetArtifactType(kind), ".json");

    private static string GetCompositionChangeKind(CompositionChangeKind kind) =>
        kind switch
        {
            CompositionChangeKind.Added => "added",
            CompositionChangeKind.Removed => "removed",
            CompositionChangeKind.Moved => "moved",
            CompositionChangeKind.Modified => "modified",
            _ => throw Failure(
                AncillaryArtifactExportFailureReason.EditingEvidenceInvalid),
        };

    private static string GetReviewActorRole(ReviewActorRole role) =>
        role switch
        {
            ReviewActorRole.Editor => "editor",
            ReviewActorRole.PerformerReviewer => "performerReviewer",
            ReviewActorRole.ContentApprover => "contentApprover",
            ReviewActorRole.BrandApprover => "brandApprover",
            ReviewActorRole.RightsApprover => "rightsApprover",
            ReviewActorRole.Administrator => "administrator",
            _ => throw Failure(
                AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid),
        };

    private static string GetReviewState(ReviewState state) =>
        state switch
        {
            ReviewState.Editing => "editing",
            ReviewState.EditorReview => "editorReview",
            ReviewState.PerformerReview => "performerReview",
            ReviewState.ContentReview => "contentReview",
            ReviewState.BrandReview => "brandReview",
            ReviewState.ChangesRequested => "changesRequested",
            ReviewState.Approved => "approved",
            ReviewState.PublicationBlocked => "publicationBlocked",
            ReviewState.FinalRendering => "finalRendering",
            ReviewState.QualityControl => "qualityControl",
            ReviewState.Exported => "exported",
            ReviewState.Published => "published",
            _ => throw Failure(
                AncillaryArtifactExportFailureReason.ReviewEvidenceInvalid),
        };

    private static string GetOriginIdentity(OriginRef origin) =>
        origin.Kind switch
        {
            OriginKind.SourceSpan when origin.SourceSpanId is not null =>
                string.Concat("0:", origin.SourceSpanId.ToString()),
            OriginKind.Asset when origin.AssetId is not null =>
                string.Concat("1:", origin.AssetId.ToString()),
            OriginKind.EditorialRecord
                when origin.EditorialRecordId is not null =>
                string.Concat("2:", origin.EditorialRecordId.ToString()),
            _ => throw Failure(
                AncillaryArtifactExportFailureReason.ProvenanceInvalid),
        };

    private static string BuildRelativePath(
        AncillaryArtifactExportRequest request,
        AncillaryArtifactKind kind) =>
        string.Concat(
            "ancillary/",
            request.CompositionVersion.Id.ToString(),
            "/",
            request.Id.ToString(),
            "/",
            GetFileName(kind));

    private static string BuildStorageReference(
        EntityId<ProjectIdTag> projectId,
        string relativePath) =>
        string.Concat(
            "projects/",
            projectId.ToString(),
            "/deliveries/",
            relativePath);

    private ExportStaging PrepareStaging(
        PreparedExport prepared,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(Path.Combine(
            ProjectFolders.GetPath(_workspacePath, prepared.Request.Project.Id),
            "staging",
            "ancillary"));
        string directory = Path.Combine(
            root,
            prepared.Request.Id.ToString());
        try
        {
            AncillaryArtifactPath.EnsureDirectoryChain(
                _workspacePath,
                root,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.StagingUnavailable);
        }

        if (AncillaryArtifactPath.EntryExistsConservatively(directory))
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.StagingConflict);
        }

        string claim = Path.Combine(
            root,
            string.Concat(
                ".",
                prepared.Request.Id.ToString(),
                ".",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                ".claim"));
        bool claimOwned = false;
        bool directoryOwned = false;
        try
        {
            _ = Directory.CreateDirectory(claim);
            claimOwned = true;
            AncillaryArtifactPath.ValidateExistingPathChain(
                _workspacePath,
                claim,
                targetIsDirectory: true);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(claim, directory);
            claimOwned = false;
            directoryOwned = true;
            AncillaryArtifactPath.ValidateExistingPathChain(
                _workspacePath,
                directory,
                targetIsDirectory: true);

            var paths = new Dictionary<AncillaryArtifactKind, string>();
            foreach (PreparedArtifact artifact in prepared.Artifacts)
            {
                paths.Add(
                    artifact.Kind,
                    Path.Combine(
                        directory,
                        string.Concat(
                            GetFileName(artifact.Kind),
                            ".partial")));
            }

            return new ExportStaging(
                directory,
                new ReadOnlyDictionary<AncillaryArtifactKind, string>(paths));
        }
        catch (Exception exception)
        {
            bool cleaned = directoryOwned
                ? AncillaryArtifactPath.TryDeleteOwnedEmptyDirectory(
                    _workspacePath,
                    directory)
                : !claimOwned ||
                    AncillaryArtifactPath.TryDeleteOwnedEmptyDirectory(
                        _workspacePath,
                        claim);
            if (!cleaned)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is AncillaryArtifactExportException)
            {
                throw;
            }

            throw Failure(
                AncillaryArtifactPath.EntryExistsConservatively(directory)
                    ? AncillaryArtifactExportFailureReason.StagingConflict
                    : AncillaryArtifactExportFailureReason.StagingUnavailable);
        }
    }

    private async Task WriteStagingAsync(
        ExportStaging staging,
        IReadOnlyList<PreparedArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        foreach (PreparedArtifact artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = staging.Paths[artifact.Kind];
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = FileBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            await using (var stream = new FileStream(path, options))
            {
                await stream.WriteAsync(
                    artifact.CanonicalBytes,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            AncillaryArtifactPath.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
        }
    }

    private async Task<AncillaryArtifactExportResult> ResolveFailedPromotionAsync(
        PreparedExport prepared,
        ExportStaging staging,
        int durableArtifactCount,
        bool preserveCancellation,
        CancellationToken cancellationToken,
        bool uncertainFailure = false)
    {
        ExistingExportInspection inspection;
        try
        {
            inspection = await InspectExistingAsync(
                prepared,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ThrowIfCleanupFailed(staging);
            throw Failure(
                AncillaryArtifactExportFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }

        ThrowIfCleanupFailed(staging);
        if (inspection.State == ExistingExportState.Exact)
        {
            return BuildResult(
                prepared,
                inspection.Artifacts,
                wasAlreadyRegistered: true);
        }

        bool reconciliationRequired = durableArtifactCount > 0 ||
            uncertainFailure ||
            inspection.State == ExistingExportState.Conflict;
        if (preserveCancellation && !reconciliationRequired)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw Failure(
            AncillaryArtifactExportFailureReason.PromotionFailed,
            reconciliationRequired);
    }

    private async Task<ExistingExportInspection> InspectExistingAsync(
        PreparedExport prepared,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredArtifact[] projectArtifacts = _listArtifacts(
                prepared.Request.Project.Id).ToArray();
            var artifactsByPath = new Dictionary<string, StoredArtifact>(
                StringComparer.Ordinal);
            foreach (StoredArtifact artifact in projectArtifacts)
            {
                if (artifact is null ||
                    !artifactsByPath.TryAdd(
                        artifact.StorageReference,
                        artifact))
                {
                    throw Failure(
                        AncillaryArtifactExportFailureReason.ArtifactConflict,
                        reconciliationRequired: true);
                }
            }

            var exact = new List<StoredArtifact>(prepared.Artifacts.Count);
            int missing = 0;
            foreach (PreparedArtifact expected in prepared.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StoredArtifact? byId = _getArtifact(expected.ArtifactId);
                artifactsByPath.TryGetValue(
                    expected.StorageReference,
                    out StoredArtifact? byPath);
                if (byId is null && byPath is null)
                {
                    missing++;
                    continue;
                }

                if (byId is null ||
                    byPath is null ||
                    byId.Id != byPath.Id ||
                    !MatchesExpected(expected, prepared.Request, byId))
                {
                    return ExistingExportInspection.Conflict;
                }

                StoredArtifact verified = await _validateArtifactAsync(
                    expected.ArtifactId,
                    cancellationToken).ConfigureAwait(false);
                if (!MatchesExpected(expected, prepared.Request, verified) ||
                    !StoredArtifactsMatch(byId, verified))
                {
                    return ExistingExportInspection.Conflict;
                }

                exact.Add(verified);
            }

            if (missing == prepared.Artifacts.Count)
            {
                return ExistingExportInspection.Missing;
            }

            if (missing != 0 || exact.Count != prepared.Artifacts.Count)
            {
                return ExistingExportInspection.Conflict;
            }

            return ExistingExportInspection.Exact(
                Array.AsReadOnly(exact.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AncillaryArtifactExportException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                reconciliationRequired: true);
        }
    }

    private async Task<IReadOnlyList<StoredArtifact>> ValidateRegisteredSetAsync(
        PreparedExport prepared,
        CancellationToken cancellationToken)
    {
        var verified = new StoredArtifact[prepared.Artifacts.Count];
        for (int index = 0; index < prepared.Artifacts.Count; index++)
        {
            PreparedArtifact expected = prepared.Artifacts[index];
            StoredArtifact artifact;
            try
            {
                artifact = await _validateArtifactAsync(
                    expected.ArtifactId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                    reconciliationRequired: true);
            }

            if (!MatchesExpected(expected, prepared.Request, artifact))
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                    reconciliationRequired: true);
            }

            verified[index] = artifact;
        }

        return Array.AsReadOnly(verified);
    }

    private static AncillaryArtifactExportResult BuildResult(
        PreparedExport prepared,
        IReadOnlyList<StoredArtifact> storedArtifacts,
        bool wasAlreadyRegistered)
    {
        if (storedArtifacts.Count != prepared.Artifacts.Count)
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                reconciliationRequired: true);
        }

        var storedById = storedArtifacts.ToDictionary(static artifact => artifact.Id);
        var outputs = new AncillaryArtifactOutput[prepared.Artifacts.Count];
        for (int index = 0; index < outputs.Length; index++)
        {
            PreparedArtifact preparedArtifact = prepared.Artifacts[index];
            if (!storedById.TryGetValue(
                    preparedArtifact.ArtifactId,
                    out StoredArtifact? stored) ||
                !MatchesExpected(
                    preparedArtifact,
                    prepared.Request,
                    stored))
            {
                throw Failure(
                    AncillaryArtifactExportFailureReason.RegisteredArtifactInvalid,
                    reconciliationRequired: true);
            }

            var manifestArtifact = new ManifestArtifact(
                GetArtifactType(preparedArtifact.Kind),
                "application/json",
                prepared.Request.Language,
                stored.ByteSize,
                stored.ContentDigest,
                stored.StorageReference);
            outputs[index] = new AncillaryArtifactOutput(
                preparedArtifact.Kind,
                preparedArtifact.ArtifactId,
                stored,
                manifestArtifact,
                preparedArtifact.PayloadDigest);
        }

        return new AncillaryArtifactExportResult(
            prepared.Request.Id,
            outputs,
            prepared.ReviewEvidenceDigest,
            wasAlreadyRegistered);
    }

    private static bool MatchesExpected(
        PreparedArtifact expected,
        AncillaryArtifactExportRequest request,
        StoredArtifact actual) =>
        actual.Id == expected.ArtifactId &&
        actual.ProjectId == request.Project.Id &&
        actual.Kind == ArtifactKind.Delivery &&
        string.Equals(
            actual.StorageReference,
            expected.StorageReference,
            StringComparison.Ordinal) &&
        actual.ContentDigest == expected.ArtifactDigest &&
        actual.ByteSize == expected.CanonicalBytes.LongLength &&
        actual.CreatedAt == request.CreatedAt;

    private static bool StoredArtifactsMatch(
        StoredArtifact first,
        StoredArtifact second) =>
        first.Id == second.Id &&
        first.ProjectId == second.ProjectId &&
        first.Kind == second.Kind &&
        string.Equals(
            first.StorageReference,
            second.StorageReference,
            StringComparison.Ordinal) &&
        first.ContentDigest == second.ContentDigest &&
        first.ByteSize == second.ByteSize &&
        first.CreatedAt == second.CreatedAt;

    private bool TryCleanupOwnedStaging(ExportStaging staging)
    {
        try
        {
            foreach (string path in staging.Paths.Values)
            {
                if (!AncillaryArtifactPath.EntryExistsConservatively(path))
                {
                    continue;
                }

                AncillaryArtifactPath.ValidateExistingPathChain(
                    _workspacePath,
                    path,
                    targetIsDirectory: false);
                File.Delete(path);
                if (AncillaryArtifactPath.EntryExistsConservatively(path))
                {
                    return false;
                }
            }

            return AncillaryArtifactPath.TryDeleteOwnedEmptyDirectory(
                _workspacePath,
                staging.DirectoryPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ThrowIfCleanupFailed(ExportStaging staging)
    {
        if (!TryCleanupOwnedStaging(staging))
        {
            throw Failure(
                AncillaryArtifactExportFailureReason.CleanupFailed,
                reconciliationRequired: true);
        }
    }

    private static GetArtifactDelegate GetArtifactDelegateFor(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.GetArtifact;
    }

    private static ListArtifactsDelegate GetListArtifactsDelegate(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.ListProjectArtifacts;
    }

    private static PromoteDelegate GetPromoteDelegate(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.PromoteAsync;
    }

    private static ValidateArtifactDelegate GetValidateArtifactDelegate(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.ValidateRegisteredAsync;
    }

    private static AncillaryArtifactExportException Failure(
        AncillaryArtifactExportFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    internal delegate StoredArtifact? GetArtifactDelegate(
        EntityId<ArtifactIdTag> artifactId);

    internal delegate IReadOnlyList<StoredArtifact> ListArtifactsDelegate(
        EntityId<ProjectIdTag> projectId);

    internal delegate Task<StoredArtifact> PromoteDelegate(
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken);

    internal delegate Task<StoredArtifact> ValidateArtifactDelegate(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken);

    private sealed record ValidatedTranscript(
        byte[] CanonicalRevisionBytes,
        ContentDigest Digest,
        IReadOnlyList<SourceSpan> TranscriptSourceSpans,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan>
            AllSourceSpans);

    private sealed record ValidatedChapter(
        TopicMetadataRevision Revision,
        OriginRef TitleOrigin);

    private sealed record ValidatedReview(
        IReadOnlyList<StoredReviewRecord> Records,
        IReadOnlyList<StoredReviewComment> Comments,
        IReadOnlyList<StoredApproval> Approvals,
        EntityId<ProjectIdTag> ProjectId,
        EntityId<CompositionVersionIdTag> CompositionVersionId,
        ContentDigest CompositionVersionDigest);

    private sealed record ValidationCatalog(
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> SourceSpans,
        IReadOnlyList<OriginRef> Origins);

    private sealed record PreparedArtifact(
        AncillaryArtifactKind Kind,
        EntityId<ArtifactIdTag> ArtifactId,
        ContentDigest PayloadDigest,
        ContentDigest ArtifactDigest,
        byte[] CanonicalBytes,
        string RelativePath,
        string StorageReference);

    private sealed record PreparedExport(
        AncillaryArtifactExportRequest Request,
        IReadOnlyList<PreparedArtifact> Artifacts,
        ContentDigest ReviewEvidenceDigest);

    private sealed record ExportStaging(
        string DirectoryPath,
        IReadOnlyDictionary<AncillaryArtifactKind, string> Paths);

    private enum ExistingExportState
    {
        Missing,
        Exact,
        Conflict,
    }

    private sealed record ExistingExportInspection(
        ExistingExportState State,
        IReadOnlyList<StoredArtifact> Artifacts)
    {
        internal static ExistingExportInspection Missing { get; } =
            new(ExistingExportState.Missing, []);

        internal static ExistingExportInspection Conflict { get; } =
            new(ExistingExportState.Conflict, []);

        internal static ExistingExportInspection Exact(
            IReadOnlyList<StoredArtifact> artifacts) =>
            new(ExistingExportState.Exact, artifacts);
    }
}

internal static class AncillaryArtifactContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string RequireText(
        string? value,
        string parameterName,
        string description)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                string.Concat(
                    description,
                    " must be nonblank without surrounding whitespace."),
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                string.Concat(
                    description,
                    " must contain valid Unicode."),
                parameterName,
                exception);
        }

        return value;
    }

    internal static string RequireLanguage(
        string? value,
        string parameterName)
    {
        string language = RequireText(
            value,
            parameterName,
            "An ancillary artifact language");
        string[] segments = language.Split('-');
        if (segments.Length == 0 ||
            segments[0].Length is < 1 or > 8 ||
            !segments[0].All(IsAsciiLetter) ||
            segments.Skip(1).Any(segment =>
                segment.Length is < 1 or > 8 ||
                !segment.All(IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException(
                "An ancillary artifact language must be a well-formed language tag.",
                parameterName);
        }

        return language;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiLetterOrDigit(char value) =>
        IsAsciiLetter(value) || value is >= '0' and <= '9';
}

internal static class AncillaryArtifactPath
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static string NormalizeExistingWorkspace(
        string path,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            OperatingSystem.IsWindows() &&
                (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                    path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                    path.StartsWith(@"\\.\", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The workspace path must be an absolute local path.",
                parameterName);
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
            if (OperatingSystem.IsWindows())
            {
                string root = Path.GetPathRoot(normalized) ?? string.Empty;
                if (normalized.AsSpan(root.Length).Contains(':'))
                {
                    throw new ArgumentException(
                        "The workspace path must not use an alternate data stream.",
                        parameterName);
                }
            }

            ValidateExistingDirectoryAncestorChain(normalized);
            return normalized;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw new ArgumentException(
                "The workspace must be an existing local directory without reparse points.",
                parameterName);
        }
    }

    internal static void EnsureDirectoryChain(
        string workspacePath,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!IsStrictDescendant(workspacePath, destination) ||
            !HasSameWindowsDriveRoot(workspacePath, destination))
        {
            throw new IOException("The directory is outside the workspace.");
        }

        string relative = Path.GetRelativePath(workspacePath, destination);
        string current = workspacePath;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExistingPathChain(
                workspacePath,
                current,
                targetIsDirectory: true);
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out _))
            {
                _ = Directory.CreateDirectory(current);
            }

            ValidateExistingPathChain(
                workspacePath,
                current,
                targetIsDirectory: true);
        }
    }

    internal static void ValidateExistingPathChain(
        string workspacePath,
        string targetPath,
        bool targetIsDirectory)
    {
        if ((!string.Equals(
                    workspacePath,
                    targetPath,
                    PathComparison) &&
                !IsStrictDescendant(workspacePath, targetPath)) ||
            !HasSameWindowsDriveRoot(workspacePath, targetPath))
        {
            throw new IOException("The path is outside the workspace.");
        }

        string? current = targetPath;
        bool isTarget = true;
        while (current is not null)
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The path contains a reparse point.");
            }

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isDevice = (attributes & FileAttributes.Device) != 0;
            if (isDevice ||
                isTarget && targetIsDirectory != isDirectory ||
                !isTarget && !isDirectory)
            {
                throw new IOException("The path entry has an invalid type.");
            }

            if (string.Equals(current, workspacePath, PathComparison))
            {
                return;
            }

            current = Path.GetDirectoryName(current);
            isTarget = false;
        }

        throw new IOException("The workspace path was not reached.");
    }

    internal static bool TryDeleteOwnedEmptyDirectory(
        string workspacePath,
        string path)
    {
        try
        {
            if (!IsStrictDescendant(workspacePath, path))
            {
                return false;
            }

            if (!TryGetAttributes(path, out _))
            {
                return true;
            }

            ValidateExistingPathChain(
                workspacePath,
                path,
                targetIsDirectory: true);
            Directory.Delete(path, recursive: false);
            return !EntryExistsConservatively(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool EntryExistsConservatively(string path)
    {
        try
        {
            return TryGetAttributes(path, out _);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void ValidateExistingDirectoryAncestorChain(string path)
    {
        string? current = path;
        while (current is not null)
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.Device) != 0)
            {
                throw new IOException("The workspace ancestor is unsafe.");
            }

            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsStrictDescendant(
        string rootPath,
        string candidatePath)
    {
        if (string.Equals(rootPath, candidatePath, PathComparison))
        {
            return false;
        }

        string prefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : string.Concat(rootPath, Path.DirectorySeparatorChar);
        return candidatePath.StartsWith(prefix, PathComparison);
    }

    private static bool HasSameWindowsDriveRoot(
        string firstPath,
        string secondPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string? firstRoot = Path.GetPathRoot(firstPath);
        string? secondRoot = Path.GetPathRoot(secondPath);
        return !string.IsNullOrEmpty(firstRoot) &&
            !string.IsNullOrEmpty(secondRoot) &&
            string.Equals(
                firstRoot,
                secondRoot,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            SecurityException or NotSupportedException or ArgumentException;
}