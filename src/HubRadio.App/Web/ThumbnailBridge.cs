using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.App.Thumbnails;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Web;

internal enum ThumbnailBridgeStatus
{
    Available,
    Forbidden,
    Unavailable,
    Stale,
    Mismatched,
    InvalidTime,
    RightsNotApproved,
    TemplateUnavailable,
    Cancelled,
    Conflict,
    Failed,
}

internal enum ThumbnailCancellationOutcome
{
    Requested,
    AlreadyRequested,
    NotRunning,
    NotFound,
}

internal sealed class ThumbnailSourceContext
{
    internal ThumbnailSourceContext(
        Project project,
        StoredArtifact sourceArtifact,
        Asset sourceAsset,
        PtsIndex sourcePtsIndex,
        ProxyFrameMap proxyFrameMap,
        ContentDigest playerEvidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceArtifact);
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentNullException.ThrowIfNull(sourcePtsIndex);
        ArgumentNullException.ThrowIfNull(proxyFrameMap);
        ArgumentNullException.ThrowIfNull(playerEvidenceDigest);

        Project = project;
        SourceArtifact = sourceArtifact;
        SourceAsset = sourceAsset;
        SourcePtsIndex = sourcePtsIndex;
        ProxyFrameMap = proxyFrameMap;
        PlayerEvidenceDigest = playerEvidenceDigest;
    }

    internal Project Project { get; }

    internal StoredArtifact SourceArtifact { get; }

    internal Asset SourceAsset { get; }

    internal PtsIndex SourcePtsIndex { get; }

    internal ProxyFrameMap ProxyFrameMap { get; }

    internal ContentDigest PlayerEvidenceDigest { get; }
}

internal sealed class ThumbnailSourceReadResult
{
    private ThumbnailSourceReadResult(
        ThumbnailBridgeStatus status,
        ThumbnailSourceContext? context)
    {
        if ((status == ThumbnailBridgeStatus.Available) != (context is not null) ||
            status is not (
                ThumbnailBridgeStatus.Available or
                ThumbnailBridgeStatus.Unavailable or
                ThumbnailBridgeStatus.Stale or
                ThumbnailBridgeStatus.Mismatched))
        {
            throw new ArgumentException(
                "The thumbnail source result is inconsistent.",
                nameof(status));
        }

        Status = status;
        Context = context;
    }

    internal ThumbnailBridgeStatus Status { get; }

    internal ThumbnailSourceContext? Context { get; }

    internal static ThumbnailSourceReadResult Available(
        ThumbnailSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ThumbnailSourceReadResult(
            ThumbnailBridgeStatus.Available,
            context);
    }

    internal static ThumbnailSourceReadResult Unavailable() =>
        new(ThumbnailBridgeStatus.Unavailable, context: null);

    internal static ThumbnailSourceReadResult Stale() =>
        new(ThumbnailBridgeStatus.Stale, context: null);

    internal static ThumbnailSourceReadResult Mismatched() =>
        new(ThumbnailBridgeStatus.Mismatched, context: null);
}

internal interface IThumbnailSourceReader
{
    Task<ThumbnailSourceReadResult> ReadAsync(
        EntityId<Core.Jobs.JobIdTag> jobId,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableThumbnailSourceReader : IThumbnailSourceReader
{
    internal static UnavailableThumbnailSourceReader Instance { get; } = new();

    private UnavailableThumbnailSourceReader()
    {
    }

    public Task<ThumbnailSourceReadResult> ReadAsync(
        EntityId<Core.Jobs.JobIdTag> jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ThumbnailSourceReadResult.Unavailable());
    }
}

/// <summary>
/// Complete immutable template context supplied only by a trusted native host.
/// </summary>
internal sealed class ApprovedThumbnailTemplateContext
{
    internal ApprovedThumbnailTemplateContext(
        ApprovedThumbnailTemplateSnapshot approval,
        AppliedBrandTemplate template,
        RenderProfile renderProfile,
        LayoutSpec layoutSpec,
        ContentDigest layoutSpecDigest)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(renderProfile);
        ArgumentNullException.ThrowIfNull(layoutSpec);
        ArgumentNullException.ThrowIfNull(layoutSpecDigest);

        Approval = approval;
        Template = template;
        RenderProfile = renderProfile;
        LayoutSpec = layoutSpec;
        LayoutSpecDigest = layoutSpecDigest;
    }

    internal ApprovedThumbnailTemplateSnapshot Approval { get; }

    internal AppliedBrandTemplate Template { get; }

    internal RenderProfile RenderProfile { get; }

    internal LayoutSpec LayoutSpec { get; }

    internal ContentDigest LayoutSpecDigest { get; }
}

/// <summary>
/// Native trust boundary for retained BrandApprover evidence and its exact
/// template, profile, and layout objects. Implementations must return one
/// atomic immutable view and must not derive approval from Web input.
/// </summary>
internal interface IApprovedThumbnailTemplateProvider
{
    Task<IReadOnlyList<ApprovedThumbnailTemplateContext>> ReadAsync(
        Project project,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableApprovedThumbnailTemplateProvider :
    IApprovedThumbnailTemplateProvider
{
    internal static UnavailableApprovedThumbnailTemplateProvider Instance { get; } =
        new();

    private UnavailableApprovedThumbnailTemplateProvider()
    {
    }

    public Task<IReadOnlyList<ApprovedThumbnailTemplateContext>> ReadAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ApprovedThumbnailTemplateContext>>(
            Array.Empty<ApprovedThumbnailTemplateContext>());
    }
}

internal sealed class StaticApprovedThumbnailTemplateProvider :
    IApprovedThumbnailTemplateProvider
{
    private readonly IReadOnlyList<ApprovedThumbnailTemplateContext> contexts;

    internal StaticApprovedThumbnailTemplateProvider(
        IReadOnlyList<ApprovedThumbnailTemplateContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ApprovedThumbnailTemplateContext[] copy = new
            ApprovedThumbnailTemplateContext[contexts.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = contexts[index] ?? throw new ArgumentException(
                "Trusted thumbnail contexts must not contain null values.",
                nameof(contexts));
        }

        this.contexts = Array.AsReadOnly(copy);
    }

    public Task<IReadOnlyList<ApprovedThumbnailTemplateContext>> ReadAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(contexts);
    }
}

internal sealed class ThumbnailTemplateSnapshot
{
    private ThumbnailTemplateSnapshot(
        ApprovedThumbnailTemplateContext context,
        string name)
    {
        Context = context;
        Name = name;
    }

    internal ApprovedThumbnailTemplateContext Context { get; }

    internal string Name { get; }

    internal ApprovedThumbnailTemplateSnapshot Approval => Context.Approval;

    internal EntityId<TemplateIdTag> TemplateId =>
        Approval.TemplateVersion.Reference.Id;

    internal ContentDigest TemplateVersionDigest =>
        Approval.TemplateVersion.Reference.VersionDigest;

    internal EntityId<RenderProfileIdTag> RenderProfileId =>
        Context.RenderProfile.Id;

    internal ContentDigest RenderProfileDigest =>
        Context.RenderProfile.ProfileDigest;

    internal EntityId<LayoutSpecIdTag> LayoutSpecId => Context.LayoutSpec.Id;

    internal ContentDigest LayoutSpecDigest => Context.LayoutSpecDigest;

    internal int Width => Context.RenderProfile.Width;

    internal int Height => Context.RenderProfile.Height;

    internal static ThumbnailTemplateSnapshot Create(
        ApprovedThumbnailTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ApprovedThumbnailTemplateSnapshot approval = context.Approval;
        BrandTemplateVersion version = approval.TemplateVersion;
        AppliedBrandTemplate template = context.Template;
        RenderProfile profile = context.RenderProfile;
        LayoutSpec layout = context.LayoutSpec;
        TemplateReference reference = version.Reference;

        _ = new ManualThumbnailTemplateBinding(
            approval,
            profile,
            context.LayoutSpecDigest);
        if (version.Kind != BrandTemplateKind.Thumbnail ||
            template.Kind != BrandTemplateKind.Thumbnail ||
            template.SeriesId != version.SeriesId ||
            !template.Reference.Equals(reference) ||
            version.ParameterDefinitions.Count != 0 ||
            template.Parameters.Count != 0 ||
            profile.LayoutSpecId != layout.Id ||
            profile.Width <= 0 ||
            profile.Height <= 0)
        {
            throw new InvalidOperationException(
                "The trusted thumbnail template context is inconsistent.");
        }

        ResolvedLayout resolved = LayoutResolver.Resolve(
            layout,
            profile.Width,
            profile.Height);
        if (resolved.Regions.Count != 1 ||
            resolved.Regions[0].Role != LayoutRegionRole.Video)
        {
            throw new InvalidOperationException(
                "The trusted thumbnail template layout is unsupported.");
        }

        string name = reference.Name;
        return new ThumbnailTemplateSnapshot(context, name);
    }
}

internal sealed class ThumbnailBridgeSnapshot
{
    private ThumbnailBridgeSnapshot(
        ContentDigest evidenceDigest,
        ThumbnailSourceContext source,
        ManualThumbnailSourceBinding sourceBinding,
        IReadOnlyList<ThumbnailTemplateSnapshot> templates)
    {
        EvidenceDigest = evidenceDigest;
        Source = source;
        SourceBinding = sourceBinding;
        Templates = templates;
    }

    internal ContentDigest EvidenceDigest { get; }

    internal ThumbnailSourceContext Source { get; }

    internal ManualThumbnailSourceBinding SourceBinding { get; }

    internal IReadOnlyList<ThumbnailTemplateSnapshot> Templates { get; }

    internal static ThumbnailBridgeSnapshot Create(
        ThumbnailSourceContext source,
        IReadOnlyList<ApprovedThumbnailTemplateContext> templateContexts)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(templateContexts);
        Asset sourceAsset = source.SourceAsset;
        StoredArtifact sourceArtifact = source.SourceArtifact;
        PtsIndex sourcePts = source.SourcePtsIndex;
        ProxyFrameMap frameMap = source.ProxyFrameMap;
        Project project = source.Project;
        ContentDigest sourcePtsDigest =
            ManualThumbnailHash.ComputeCanonicalJsonDigest(
                sourcePts.CanonicalJson);
        ContentDigest frameMapDigest =
            ManualThumbnailHash.ComputeCanonicalJsonDigest(
                frameMap.CanonicalJson);
        if (project.Assets.Count(asset => asset.Id == sourceAsset.Id) != 1 ||
            sourceArtifact.ProjectId != project.Id ||
            sourceArtifact.Kind != ArtifactKind.Original ||
            sourceArtifact.ContentDigest != sourceAsset.ContentDigest ||
            sourceArtifact.ByteSize != sourceAsset.ByteSize ||
            sourcePts.CandidateId != sourceAsset.Id ||
            sourcePts.ProjectId != project.Id ||
            sourcePts.ContentDigest != sourceAsset.ContentDigest ||
            sourcePts.ByteSize != sourceAsset.ByteSize ||
            frameMap.SourceAssetId != sourceAsset.Id ||
            frameMap.ProjectId != project.Id ||
            frameMap.SourceContentDigest != sourceAsset.ContentDigest ||
            frameMap.SourcePtsIndexDigest != sourcePtsDigest ||
            frameMap.SourceFrameCount != sourcePts.Entries.Count ||
            frameMap.SourcePresentationStart != sourcePts.PresentationStart ||
            frameMap.SourcePresentationEnd != sourcePts.PresentationEnd)
        {
            throw new InvalidOperationException(
                "The thumbnail source context is inconsistent.");
        }

        var sourceBinding = new ManualThumbnailSourceBinding(
            project.Id,
            sourceArtifact.Id,
            sourceAsset,
            sourcePtsDigest,
            frameMapDigest);
        ThumbnailTemplateSnapshot[] templates = templateContexts
            .Select(ThumbnailTemplateSnapshot.Create)
            .OrderBy(static template => template.TemplateId.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        if (templates.Select(static template => template.TemplateId)
            .Distinct()
            .Count() != templates.Length)
        {
            throw new InvalidOperationException(
                "The trusted thumbnail template set contains duplicate template IDs.");
        }

        IReadOnlyList<ThumbnailTemplateSnapshot> readOnlyTemplates =
            Array.AsReadOnly(templates);
        ContentDigest digest = ComputeEvidenceDigest(
            source,
            sourceBinding,
            readOnlyTemplates);
        return new ThumbnailBridgeSnapshot(
            digest,
            source,
            sourceBinding,
            readOnlyTemplates);
    }

    private static ContentDigest ComputeEvidenceDigest(
        ThumbnailSourceContext source,
        ManualThumbnailSourceBinding binding,
        IReadOnlyList<ThumbnailTemplateSnapshot> templates)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("thumbnail-bridge-evidence-v1");
        writer.WriteStringValue(source.PlayerEvidenceDigest.Hex);
        writer.WriteStringValue(binding.ProjectId.ToString());
        writer.WriteStringValue(binding.SourceArtifactId.ToString());
        writer.WriteStringValue(binding.SourceAssetId.ToString());
        writer.WriteStringValue(binding.SourceContentDigest.Hex);
        writer.WriteNumberValue(binding.SourceByteSize);
        writer.WriteStringValue(binding.SourceRightsDigest.Hex);
        writer.WriteStringValue(binding.ObservedRightsStatus.ToString());
        writer.WriteBooleanValue(binding.ObservedExternalPublicationAllowed);
        writer.WriteStringValue(binding.SourcePtsIndexDigest.Hex);
        writer.WriteStringValue(binding.ProxyFrameMapDigest.Hex);
        foreach (ThumbnailTemplateSnapshot template in templates)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(template.Approval.Id.ToString());
            writer.WriteStringValue(template.Approval.ApprovalDigest.Hex);
            writer.WriteStringValue(template.TemplateId.ToString());
            writer.WriteStringValue(template.TemplateVersionDigest.Hex);
            writer.WriteStringValue(template.RenderProfileId.ToString());
            writer.WriteStringValue(template.RenderProfileDigest.Hex);
            writer.WriteStringValue(template.LayoutSpecId.ToString());
            writer.WriteStringValue(template.LayoutSpecDigest.Hex);
            writer.WriteNumberValue(template.Width);
            writer.WriteNumberValue(template.Height);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.Flush();
        return ContentDigest.Compute(buffer.WrittenSpan);
    }
}

internal sealed class ThumbnailBridgeReadResult
{
    private ThumbnailBridgeReadResult(
        ThumbnailBridgeStatus status,
        ThumbnailBridgeSnapshot? snapshot)
    {
        if ((status == ThumbnailBridgeStatus.Available) != (snapshot is not null) ||
            status is not (
                ThumbnailBridgeStatus.Available or
                ThumbnailBridgeStatus.Forbidden or
                ThumbnailBridgeStatus.Unavailable or
                ThumbnailBridgeStatus.Stale or
                ThumbnailBridgeStatus.Mismatched))
        {
            throw new ArgumentException(
                "The thumbnail read result is inconsistent.",
                nameof(status));
        }

        Status = status;
        Snapshot = snapshot;
    }

    internal ThumbnailBridgeStatus Status { get; }

    internal ThumbnailBridgeSnapshot? Snapshot { get; }

    internal static ThumbnailBridgeReadResult Available(
        ThumbnailBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ThumbnailBridgeReadResult(
            ThumbnailBridgeStatus.Available,
            snapshot);
    }

    internal static ThumbnailBridgeReadResult Failure(
        ThumbnailBridgeStatus status) => new(status, snapshot: null);
}

internal sealed class ThumbnailBridgeOutput
{
    internal ThumbnailBridgeOutput(
        ManualThumbnailResult result,
        PlayerMediaAssetReference mediaReference)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(mediaReference);
        if (mediaReference.Kind != PlayerMediaAssetKind.GeneratedThumbnail ||
            mediaReference.ProjectId != result.ProjectId ||
            mediaReference.ArtifactId != result.ArtifactId ||
            mediaReference.ContentDigest != result.OutputContentDigest)
        {
            throw new ArgumentException(
                "The thumbnail output media reference is inconsistent.",
                nameof(mediaReference));
        }

        Result = result;
        MediaReference = mediaReference;
    }

    internal ManualThumbnailResult Result { get; }

    internal PlayerMediaAssetReference MediaReference { get; }
}

internal sealed class ThumbnailBridgeGenerationResult
{
    private ThumbnailBridgeGenerationResult(
        ThumbnailBridgeStatus status,
        ThumbnailBridgeOutput? output)
    {
        if ((status == ThumbnailBridgeStatus.Available) != (output is not null) ||
            status is not (
                ThumbnailBridgeStatus.Available or
                ThumbnailBridgeStatus.Forbidden or
                ThumbnailBridgeStatus.Unavailable or
                ThumbnailBridgeStatus.Stale or
                ThumbnailBridgeStatus.Mismatched or
                ThumbnailBridgeStatus.InvalidTime or
                ThumbnailBridgeStatus.RightsNotApproved or
                ThumbnailBridgeStatus.TemplateUnavailable or
                ThumbnailBridgeStatus.Cancelled or
                ThumbnailBridgeStatus.Conflict or
                ThumbnailBridgeStatus.Failed))
        {
            throw new ArgumentException(
                "The thumbnail generation result is inconsistent.",
                nameof(status));
        }

        Status = status;
        Output = output;
    }

    internal ThumbnailBridgeStatus Status { get; }

    internal ThumbnailBridgeOutput? Output { get; }

    internal static ThumbnailBridgeGenerationResult Succeeded(
        ThumbnailBridgeOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new ThumbnailBridgeGenerationResult(
            ThumbnailBridgeStatus.Available,
            output);
    }

    internal static ThumbnailBridgeGenerationResult Failure(
        ThumbnailBridgeStatus status) => new(status, output: null);
}

internal interface IThumbnailAccessAuthorization
{
    void DemandRead();

    string DemandGenerate();
}

internal sealed class ThumbnailAccessAuthorization :
    IThumbnailAccessAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal ThumbnailAccessAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, GetActorProvider(actorProvider))
    {
    }

    internal ThumbnailAccessAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public void DemandRead()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.PreviewProject);
    }

    public string DemandGenerate()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.StartApprovedRender);
        return actorSid;
    }

    private static Func<string> GetActorProvider(
        WindowsActorProvider actorProvider)
    {
        ArgumentNullException.ThrowIfNull(actorProvider);
        return actorProvider.GetCurrentSid;
    }
}

internal sealed class DeniedThumbnailAccessAuthorization :
    IThumbnailAccessAuthorization
{
    internal static DeniedThumbnailAccessAuthorization Instance { get; } = new();

    private DeniedThumbnailAccessAuthorization()
    {
    }

    public void DemandRead() => throw new AuthorizationException(
        AuthorizationOperation.PreviewProject,
        actingRole: null);

    public string DemandGenerate() => throw new AuthorizationException(
        AuthorizationOperation.StartApprovedRender,
        actingRole: null);
}

internal readonly record struct ThumbnailGenerationFingerprint(
    EntityId<Core.Jobs.JobIdTag> JobId,
    ContentDigest EvidenceDigest,
    ContentDigest PlayerEvidenceDigest,
    EntityId<TemplateIdTag> TemplateId,
    int ProxyFrameSequence,
    string ActorId);

internal sealed class ThumbnailGenerationCoordinator
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, GenerationRegistration> registrations = [];

    internal Task<ThumbnailBridgeGenerationResult> RunAsync(
        Guid generationId,
        ThumbnailGenerationFingerprint fingerprint,
        DateTimeOffset proposedSelectedAt,
        Func<
            DateTimeOffset,
            CancellationToken,
            Task<ThumbnailBridgeGenerationResult>> executeAsync,
        CancellationToken lifetimeCancellation)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A thumbnail generation ID must not be empty.",
                nameof(generationId));
        }

        ArgumentNullException.ThrowIfNull(executeAsync);
        lifetimeCancellation.ThrowIfCancellationRequested();
        DateTimeOffset canonicalSelectedAt = proposedSelectedAt
            .ToUniversalTime();

        lock (gate)
        {
            bool createdRegistration = false;
            if (!registrations.TryGetValue(
                    generationId,
                    out GenerationRegistration? registration))
            {
                registration = new GenerationRegistration(
                    fingerprint,
                    canonicalSelectedAt);
                registrations.Add(generationId, registration);
                createdRegistration = true;
            }
            else if (registration.Fingerprint != fingerprint)
            {
                return Task.FromResult(
                    ThumbnailBridgeGenerationResult.Failure(
                        ThumbnailBridgeStatus.Conflict));
            }

            if (registration.TerminalResult is not null)
            {
                return Task.FromResult(registration.TerminalResult);
            }

            GenerationOperation? existing = registration.ActiveOperation;
            if (existing is not null && !existing.Task.IsCompleted)
            {
                return existing.Task;
            }

            if (existing is not null)
            {
                CaptureTerminalResult(registration, existing.Task);
                registration.ActiveOperation = null;
                existing.MarkCompleted();
                existing.Dispose();
                if (registration.TerminalResult is not null)
                {
                    return Task.FromResult(registration.TerminalResult);
                }
            }

            var operation = new GenerationOperation(
                lifetimeCancellation);
            registration.ActiveOperation = operation;
            try
            {
                operation.Start(token => executeAsync(
                    registration.SelectedAt,
                    token));
                _ = ObserveCompletionAsync(
                    generationId,
                    registration,
                    operation);
            }
            catch
            {
                registration.ActiveOperation = null;
                if (createdRegistration)
                {
                    registrations.Remove(generationId);
                }

                operation.Dispose();
                throw;
            }

            return operation.Task;
        }
    }

    internal ThumbnailCancellationOutcome Cancel(Guid generationId)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A thumbnail generation ID must not be empty.",
                nameof(generationId));
        }

        GenerationOperation? operation;
        lock (gate)
        {
            if (!registrations.TryGetValue(
                    generationId,
                    out GenerationRegistration? registration))
            {
                return ThumbnailCancellationOutcome.NotFound;
            }

            operation = registration.ActiveOperation;
            if (operation is null)
            {
                return ThumbnailCancellationOutcome.NotRunning;
            }

            if (operation.Task.IsCompleted)
            {
                CaptureTerminalResult(registration, operation.Task);
                registration.ActiveOperation = null;
                operation.MarkCompleted();
                operation.Dispose();
                return ThumbnailCancellationOutcome.NotRunning;
            }
        }

        return operation.RequestCancellation();
    }

    private async Task ObserveCompletionAsync(
        Guid generationId,
        GenerationRegistration registration,
        GenerationOperation operation)
    {
        ThumbnailBridgeGenerationResult? result = null;
        try
        {
            result = await operation.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A lifetime cancellation is observed by the request waiter. The
            // registration remains available for a same-binding retry.
        }

        Complete(generationId, registration, operation, result);
    }

    private void Complete(
        Guid generationId,
        GenerationRegistration registration,
        GenerationOperation operation,
        ThumbnailBridgeGenerationResult? result)
    {
        operation.MarkCompleted();
        lock (gate)
        {
            if (registrations.TryGetValue(
                    generationId,
                    out GenerationRegistration? current) &&
                ReferenceEquals(current, registration) &&
                ReferenceEquals(current.ActiveOperation, operation))
            {
                current.ActiveOperation = null;
                if (result is not null)
                {
                    CaptureTerminalResult(current, result);
                }
            }
        }

        operation.Dispose();
    }

    private static void CaptureTerminalResult(
        GenerationRegistration registration,
        Task<ThumbnailBridgeGenerationResult> task)
    {
        if (task.Status == TaskStatus.RanToCompletion)
        {
            CaptureTerminalResult(registration, task.Result);
        }
    }

    private static void CaptureTerminalResult(
        GenerationRegistration registration,
        ThumbnailBridgeGenerationResult result)
    {
        if (result.Status == ThumbnailBridgeStatus.Cancelled)
        {
            registration.TerminalResult = result;
        }
    }

    private sealed class GenerationRegistration(
        ThumbnailGenerationFingerprint fingerprint,
        DateTimeOffset selectedAt)
    {
        internal ThumbnailGenerationFingerprint Fingerprint { get; } =
            fingerprint;

        internal DateTimeOffset SelectedAt { get; } = selectedAt;

        internal GenerationOperation? ActiveOperation { get; set; }

        internal ThumbnailBridgeGenerationResult? TerminalResult { get; set; }
    }

    private sealed class GenerationOperation : IDisposable
    {
        private readonly object cancellationGate = new();
        private readonly CancellationTokenSource manualCancellation = new();
        private readonly CancellationToken lifetimeCancellation;
        private bool cancellationRequested;
        private bool isCompleted;
        private bool isDisposed;
        private Task<ThumbnailBridgeGenerationResult>? task;

        internal GenerationOperation(
            CancellationToken lifetimeCancellation)
        {
            this.lifetimeCancellation = lifetimeCancellation;
        }

        internal Task<ThumbnailBridgeGenerationResult> Task =>
            task ?? throw new InvalidOperationException(
                "The thumbnail generation operation has not started.");

        internal void Start(
            Func<CancellationToken, Task<ThumbnailBridgeGenerationResult>>
                executeAsync)
        {
            ArgumentNullException.ThrowIfNull(executeAsync);
            if (task is not null)
            {
                throw new InvalidOperationException(
                    "The thumbnail generation operation is already started.");
            }

            Task<ThumbnailBridgeGenerationResult> started =
                System.Threading.Tasks.Task.Run(
                () => ExecuteAsync(executeAsync),
                CancellationToken.None);
            task = started;
        }

        internal ThumbnailCancellationOutcome RequestCancellation()
        {
            lock (cancellationGate)
            {
                if (isCompleted || task?.IsCompleted == true)
                {
                    isCompleted = true;
                    return ThumbnailCancellationOutcome.NotRunning;
                }

                if (cancellationRequested)
                {
                    return ThumbnailCancellationOutcome.AlreadyRequested;
                }

                cancellationRequested = true;
                manualCancellation.Cancel();
                return ThumbnailCancellationOutcome.Requested;
            }
        }

        internal void MarkCompleted()
        {
            lock (cancellationGate)
            {
                isCompleted = true;
            }
        }

        private async Task<ThumbnailBridgeGenerationResult> ExecuteAsync(
            Func<CancellationToken, Task<ThumbnailBridgeGenerationResult>>
                executeAsync)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    lifetimeCancellation,
                    manualCancellation.Token);
            try
            {
                ThumbnailBridgeGenerationResult? result =
                    await executeAsync(linked.Token).ConfigureAwait(false);
                return result ?? ThumbnailBridgeGenerationResult.Failure(
                    ThumbnailBridgeStatus.Failed);
            }
            catch (OperationCanceledException)
                when (lifetimeCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(lifetimeCancellation);
            }
            catch (OperationCanceledException)
                when (manualCancellation.IsCancellationRequested)
            {
                return ThumbnailBridgeGenerationResult.Failure(
                    ThumbnailBridgeStatus.Cancelled);
            }
            catch (Exception)
            {
                return ThumbnailBridgeGenerationResult.Failure(
                    ThumbnailBridgeStatus.Failed);
            }
        }

        public void Dispose()
        {
            lock (cancellationGate)
            {
                isCompleted = true;
                if (isDisposed)
                {
                    return;
                }

                manualCancellation.Dispose();
                isDisposed = true;
            }
        }
    }
}

internal sealed class ThumbnailBridgeRequestHandler
{
    private static readonly TimeSpan GenerationTimeout =
        ProcessRunRequest.MaximumTimeout;

    private readonly IThumbnailSourceReader sourceReader;
    private readonly IApprovedThumbnailTemplateProvider templateProvider;
    private readonly IThumbnailAccessAuthorization authorization;
    private readonly Func<
        ManualThumbnailRequest,
        ManualThumbnailContext,
        TimeSpan,
        CancellationToken,
        Task<ManualThumbnailResult>> generateAsync;
    private readonly ThumbnailGenerationCoordinator generationCoordinator;
    private readonly TimeProvider timeProvider;

    internal ThumbnailBridgeRequestHandler(
        IThumbnailSourceReader sourceReader,
        IApprovedThumbnailTemplateProvider templateProvider,
        IThumbnailAccessAuthorization authorization,
        ManualThumbnailService thumbnailService,
        TimeProvider? timeProvider = null)
        : this(
            sourceReader,
            templateProvider,
            authorization,
            GetGenerateDelegate(thumbnailService),
            new ThumbnailGenerationCoordinator(),
            timeProvider)
    {
    }

    internal ThumbnailBridgeRequestHandler(
        IThumbnailSourceReader sourceReader,
        IApprovedThumbnailTemplateProvider templateProvider,
        IThumbnailAccessAuthorization authorization,
        Func<
            ManualThumbnailRequest,
            ManualThumbnailContext,
            TimeSpan,
            CancellationToken,
            Task<ManualThumbnailResult>> generateAsync,
        ThumbnailGenerationCoordinator? generationCoordinator = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sourceReader);
        ArgumentNullException.ThrowIfNull(templateProvider);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(generateAsync);
        this.sourceReader = sourceReader;
        this.templateProvider = templateProvider;
        this.authorization = authorization;
        this.generateAsync = generateAsync;
        this.generationCoordinator = generationCoordinator ??
            new ThumbnailGenerationCoordinator();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return request.Method switch
        {
            BridgeMethod.ThumbnailRead => await HandleReadAsync(
                request,
                cancellationToken).ConfigureAwait(false),
            BridgeMethod.ThumbnailGenerate => await HandleGenerateAsync(
                request,
                cancellationToken).ConfigureAwait(false),
            BridgeMethod.ThumbnailCancel => HandleCancel(request),
            _ => throw new InvalidOperationException(
                "The parsed thumbnail bridge method is unsupported."),
        };
    }

    private async Task<string> HandleReadAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        ThumbnailBridgeReadResult result;
        try
        {
            authorization.DemandRead();
            result = await ReadCurrentAsync(
                request.JobId ?? throw new InvalidOperationException(
                    "A thumbnail read request is missing its job identity."),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AuthorizationException)
        {
            result = ThumbnailBridgeReadResult.Failure(
                ThumbnailBridgeStatus.Forbidden);
        }
        catch (Exception)
        {
            result = ThumbnailBridgeReadResult.Failure(
                ThumbnailBridgeStatus.Unavailable);
        }

        return ThumbnailBridgeJson.WriteReadResponse(request, result);
    }

    private async Task<string> HandleGenerateAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        string actorId;
        try
        {
            actorId = authorization.DemandGenerate();
        }
        catch (AuthorizationException)
        {
            return ThumbnailBridgeJson.WriteGenerationResponse(
                request,
                ThumbnailBridgeGenerationResult.Failure(
                    ThumbnailBridgeStatus.Forbidden));
        }
        catch (Exception)
        {
            return ThumbnailBridgeJson.WriteGenerationResponse(
                request,
                ThumbnailBridgeGenerationResult.Failure(
                    ThumbnailBridgeStatus.Unavailable));
        }

        EntityId<Core.Jobs.JobIdTag> jobId = request.JobId
            ?? throw new InvalidOperationException(
                "A thumbnail generation request is missing its job identity.");
        ContentDigest evidenceDigest = request.EvidenceDigest
            ?? throw new InvalidOperationException(
                "A thumbnail generation request is missing its evidence digest.");
        ContentDigest playerEvidenceDigest = request.PlayerEvidenceDigest
            ?? throw new InvalidOperationException(
                "A thumbnail generation request is missing player evidence.");
        EntityId<TemplateIdTag> templateId = request.TemplateId
            ?? throw new InvalidOperationException(
                "A thumbnail generation request is missing its template identity.");
        int proxyFrameSequence = request.ProxyFrameSequence
            ?? throw new InvalidOperationException(
                "A thumbnail generation request is missing its frame sequence.");
        Guid generationId = request.GenerationId
            ?? throw new InvalidOperationException(
                "A thumbnail generation request is missing its generation identity.");
        var fingerprint = new ThumbnailGenerationFingerprint(
            jobId,
            evidenceDigest,
            playerEvidenceDigest,
            templateId,
            proxyFrameSequence,
            actorId);
        DateTimeOffset proposedSelectedAt;
        try
        {
            proposedSelectedAt = timeProvider.GetUtcNow().ToUniversalTime();
        }
        catch (Exception)
        {
            return ThumbnailBridgeJson.WriteGenerationResponse(
                request,
                ThumbnailBridgeGenerationResult.Failure(
                    ThumbnailBridgeStatus.Failed));
        }

        ThumbnailBridgeGenerationResult result = await generationCoordinator
            .RunAsync(
                generationId,
                fingerprint,
                proposedSelectedAt,
                (selectedAt, token) => GenerateCoreAsync(
                    generationId,
                    fingerprint,
                    selectedAt,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        return ThumbnailBridgeJson.WriteGenerationResponse(request, result);
    }

    private string HandleCancel(BridgeRequest request)
    {
        try
        {
            _ = authorization.DemandGenerate();
        }
        catch (AuthorizationException)
        {
            return ThumbnailBridgeJson.WriteCancellationResponse(
                request,
                ThumbnailBridgeStatus.Forbidden,
                outcome: null);
        }
        catch (Exception)
        {
            return ThumbnailBridgeJson.WriteCancellationResponse(
                request,
                ThumbnailBridgeStatus.Unavailable,
                outcome: null);
        }

        Guid generationId = request.GenerationId
            ?? throw new InvalidOperationException(
                "A thumbnail cancellation request is missing its generation identity.");
        ThumbnailCancellationOutcome outcome = generationCoordinator.Cancel(
            generationId);
        return ThumbnailBridgeJson.WriteCancellationResponse(
            request,
            ThumbnailBridgeStatus.Available,
            outcome);
    }

    private async Task<ThumbnailBridgeGenerationResult> GenerateCoreAsync(
        Guid generationId,
        ThumbnailGenerationFingerprint fingerprint,
        DateTimeOffset selectedAt,
        CancellationToken cancellationToken)
    {
        ThumbnailBridgeReadResult current = await ReadCurrentAsync(
            fingerprint.JobId,
            cancellationToken).ConfigureAwait(false);
        if (current.Status != ThumbnailBridgeStatus.Available)
        {
            return ThumbnailBridgeGenerationResult.Failure(current.Status);
        }

        ThumbnailBridgeSnapshot snapshot = current.Snapshot!;
        if (snapshot.EvidenceDigest != fingerprint.EvidenceDigest ||
            snapshot.Source.PlayerEvidenceDigest !=
                fingerprint.PlayerEvidenceDigest)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                ThumbnailBridgeStatus.Stale);
        }

        if (snapshot.SourceBinding.ObservedRightsStatus !=
            RightsStatus.Approved ||
            !snapshot.SourceBinding.ObservedExternalPublicationAllowed)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                ThumbnailBridgeStatus.RightsNotApproved);
        }

        ThumbnailTemplateSnapshot? template = snapshot.Templates
            .SingleOrDefault(candidate => candidate.TemplateId ==
                fingerprint.TemplateId);
        if (template is null)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                ThumbnailBridgeStatus.TemplateUnavailable);
        }

        ProxyFrameMapEntry mapped;
        try
        {
            mapped = snapshot.Source.ProxyFrameMap.MapProxyFrameToSource(
                fingerprint.ProxyFrameSequence);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                ThumbnailBridgeStatus.InvalidTime);
        }

        PtsIndex sourcePts = snapshot.Source.SourcePtsIndex;
        if (mapped.SourcePresentationTime < sourcePts.PresentationStart ||
            mapped.SourcePresentationTime >= sourcePts.PresentationEnd)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                ThumbnailBridgeStatus.InvalidTime);
        }

        if ((uint)mapped.SourceFrameSequence >=
                (uint)sourcePts.Entries.Count ||
            sourcePts.Entries[mapped.SourceFrameSequence].PresentationTime !=
                mapped.SourcePresentationTime)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                ThumbnailBridgeStatus.Mismatched);
        }

        var selection = new ManualThumbnailFrameSelection(
            fingerprint.ProxyFrameSequence,
            mapped.SourceFrameSequence,
            mapped.SourcePresentationTime,
            fingerprint.ActorId,
            selectedAt);
        var templateBinding = new ManualThumbnailTemplateBinding(
            template.Approval,
            template.Context.RenderProfile,
            template.Context.LayoutSpecDigest);
        var thumbnailRequest = new ManualThumbnailRequest(
            EntityId<ManualThumbnailRequestIdTag>.From(generationId),
            EntityId<ArtifactIdTag>.From(generationId),
            snapshot.SourceBinding,
            selection,
            templateBinding);
        var context = new ManualThumbnailContext(
            snapshot.Source.Project,
            snapshot.Source.SourceArtifact,
            sourcePts,
            snapshot.Source.ProxyFrameMap,
            template.Approval.TemplateVersion,
            template.Context.Template,
            template.Approval,
            template.Context.RenderProfile,
            template.Context.LayoutSpec,
            template.Context.LayoutSpecDigest);

        try
        {
            ManualThumbnailResult result = await generateAsync(
                thumbnailRequest,
                context,
                GenerationTimeout,
                cancellationToken).ConfigureAwait(false);
            var reference = PlayerMediaAssetReference.GeneratedThumbnail(
                result.ProjectId,
                result.ArtifactId,
                result.OutputContentDigest);
            return ThumbnailBridgeGenerationResult.Succeeded(
                new ThumbnailBridgeOutput(result, reference));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ManualThumbnailException exception)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                MapFailure(exception.Reason));
        }
        catch (Exception)
        {
            return ThumbnailBridgeGenerationResult.Failure(
                ThumbnailBridgeStatus.Failed);
        }
    }

    private async Task<ThumbnailBridgeReadResult> ReadCurrentAsync(
        EntityId<Core.Jobs.JobIdTag> jobId,
        CancellationToken cancellationToken)
    {
        ThumbnailSourceReadResult source = await sourceReader.ReadAsync(
            jobId,
            cancellationToken).ConfigureAwait(false) ??
            ThumbnailSourceReadResult.Unavailable();
        if (source.Status != ThumbnailBridgeStatus.Available)
        {
            return ThumbnailBridgeReadResult.Failure(source.Status);
        }

        IReadOnlyList<ApprovedThumbnailTemplateContext>? templates =
            await templateProvider.ReadAsync(
                source.Context!.Project,
                cancellationToken).ConfigureAwait(false);
        if (templates is null || templates.Count == 0)
        {
            return ThumbnailBridgeReadResult.Failure(
                ThumbnailBridgeStatus.Unavailable);
        }

        ThumbnailBridgeSnapshot snapshot;
        try
        {
            snapshot = ThumbnailBridgeSnapshot.Create(
                source.Context,
                templates);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                OverflowException)
        {
            return ThumbnailBridgeReadResult.Failure(
                ThumbnailBridgeStatus.Mismatched);
        }

        return ThumbnailBridgeReadResult.Available(snapshot);
    }

    private static ThumbnailBridgeStatus MapFailure(
        ManualThumbnailFailureReason reason) => reason switch
        {
            ManualThumbnailFailureReason.SelectedFrameInvalid =>
                ThumbnailBridgeStatus.InvalidTime,
            ManualThumbnailFailureReason.SourceRightsNotApproved =>
                ThumbnailBridgeStatus.RightsNotApproved,
            ManualThumbnailFailureReason.TemplateNotApproved =>
                ThumbnailBridgeStatus.TemplateUnavailable,
            ManualThumbnailFailureReason.SourceBindingStale or
            ManualThumbnailFailureReason.ProxyMappingStale or
            ManualThumbnailFailureReason.TemplateBindingStale or
            ManualThumbnailFailureReason.ProfileBindingStale or
            ManualThumbnailFailureReason.LayoutBindingStale =>
                ThumbnailBridgeStatus.Stale,
            ManualThumbnailFailureReason.InputBindingMismatch or
            ManualThumbnailFailureReason.TemplateUnsupported =>
                ThumbnailBridgeStatus.Mismatched,
            ManualThumbnailFailureReason.DuplicateRequest or
            ManualThumbnailFailureReason.StagingConflict =>
                ThumbnailBridgeStatus.Conflict,
            ManualThumbnailFailureReason.RegisteredSourceInvalid =>
                ThumbnailBridgeStatus.Unavailable,
            _ => ThumbnailBridgeStatus.Failed,
        };

    private static Func<
        ManualThumbnailRequest,
        ManualThumbnailContext,
        TimeSpan,
        CancellationToken,
        Task<ManualThumbnailResult>> GetGenerateDelegate(
            ManualThumbnailService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.GenerateAsync;
    }
}

internal static class ThumbnailBridgeJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string WriteReadResponse(
        BridgeRequest request,
        ThumbnailBridgeReadResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return WriteReadResponseCore(request, result);
        }
        catch (InvalidOperationException)
            when (result.Status == ThumbnailBridgeStatus.Available)
        {
            return WriteReadResponseCore(
                request,
                ThumbnailBridgeReadResult.Failure(
                    ThumbnailBridgeStatus.Unavailable));
        }
    }

    internal static string WriteGenerationResponse(
        BridgeRequest request,
        ThumbnailBridgeGenerationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return WriteGenerationResponseCore(request, result);
        }
        catch (InvalidOperationException)
            when (result.Status == ThumbnailBridgeStatus.Available)
        {
            return WriteGenerationResponseCore(
                request,
                ThumbnailBridgeGenerationResult.Failure(
                    ThumbnailBridgeStatus.Unavailable));
        }
    }

    internal static string WriteCancellationResponse(
        BridgeRequest request,
        ThumbnailBridgeStatus status,
        ThumbnailCancellationOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((status == ThumbnailBridgeStatus.Available) != (outcome is not null) ||
            status is not (
                ThumbnailBridgeStatus.Available or
                ThumbnailBridgeStatus.Forbidden or
                ThumbnailBridgeStatus.Unavailable))
        {
            throw new ArgumentException(
                "The thumbnail cancellation response is inconsistent.",
                nameof(status));
        }

        return WriteResponse(
            request,
            writer =>
            {
                writer.WriteStartObject("result");
                writer.WriteString("status", GetStatusText(status));
                if (outcome is ThumbnailCancellationOutcome value)
                {
                    writer.WriteString("outcome", GetOutcomeText(value));
                }
                else
                {
                    writer.WriteNull("outcome");
                }

                writer.WriteEndObject();
            });
    }

    private static string WriteReadResponseCore(
        BridgeRequest request,
        ThumbnailBridgeReadResult result) => WriteResponse(
            request,
            writer =>
            {
                writer.WriteStartObject("result");
                writer.WriteString("status", GetStatusText(result.Status));
                writer.WritePropertyName("snapshot");
                if (result.Snapshot is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteSnapshot(writer, result.Snapshot);
                }

                writer.WriteEndObject();
            });

    private static string WriteGenerationResponseCore(
        BridgeRequest request,
        ThumbnailBridgeGenerationResult result) => WriteResponse(
            request,
            writer =>
            {
                writer.WriteStartObject("result");
                writer.WriteString("status", GetStatusText(result.Status));
                writer.WritePropertyName("output");
                if (result.Output is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteOutput(writer, result.Output);
                }

                writer.WriteEndObject();
            });

    private static string WriteResponse(
        BridgeRequest request,
        Action<Utf8JsonWriter> writeResult)
    {
        var buffer = new PlayerBridgeJson.BoundedBufferWriter(
            BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("version", BridgeProtocol.Version);
        writer.WriteString("requestId", request.RequestId.ToString("D"));
        writer.WriteString("method", BridgeProtocol.GetMethodName(request.Method));
        writeResult(writer);
        writer.WriteEndObject();
        writer.Flush();
        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        ThumbnailBridgeSnapshot snapshot)
    {
        ThumbnailSourceContext source = snapshot.Source;
        ManualThumbnailSourceBinding binding = snapshot.SourceBinding;
        writer.WriteStartObject();
        writer.WriteString("evidenceDigest", snapshot.EvidenceDigest.Hex);
        writer.WriteString(
            "playerEvidenceDigest",
            source.PlayerEvidenceDigest.Hex);
        writer.WriteString("projectId", binding.ProjectId.ToString());
        writer.WriteString("sourceArtifactId", binding.SourceArtifactId.ToString());
        writer.WriteString("sourceAssetId", binding.SourceAssetId.ToString());
        writer.WriteString("sourceContentDigest", binding.SourceContentDigest.Hex);
        writer.WriteString("sourceRightsDigest", binding.SourceRightsDigest.Hex);
        writer.WriteString("sourcePtsIndexDigest", binding.SourcePtsIndexDigest.Hex);
        writer.WriteString("proxyFrameMapDigest", binding.ProxyFrameMapDigest.Hex);
        writer.WritePropertyName("sourcePresentationStart");
        WriteRational(writer, source.SourcePtsIndex.PresentationStart);
        writer.WritePropertyName("sourcePresentationEnd");
        WriteRational(writer, source.SourcePtsIndex.PresentationEnd);
        writer.WriteStartObject("rights");
        writer.WriteString(
            "status",
            GetRightsStatusText(binding.ObservedRightsStatus));
        writer.WriteBoolean(
            "externalPublicationAllowed",
            binding.ObservedExternalPublicationAllowed);
        writer.WriteEndObject();
        writer.WriteStartArray("templates");
        foreach (ThumbnailTemplateSnapshot template in snapshot.Templates)
        {
            writer.WriteStartObject();
            writer.WriteString("approvalId", template.Approval.Id.ToString());
            writer.WriteString(
                "approvalDigest",
                template.Approval.ApprovalDigest.Hex);
            writer.WriteString("approvalRole", "brandApprover");
            writer.WriteString(
                "approvedAt",
                FormatTimestamp(template.Approval.ApprovedAt));
            writer.WriteString("templateId", template.TemplateId.ToString());
            writer.WriteString("name", template.Name);
            writer.WriteString(
                "templateVersionDigest",
                template.TemplateVersionDigest.Hex);
            writer.WriteString(
                "renderProfileId",
                template.RenderProfileId.ToString());
            writer.WriteString(
                "renderProfileDigest",
                template.RenderProfileDigest.Hex);
            writer.WriteString("layoutSpecId", template.LayoutSpecId.ToString());
            writer.WriteString(
                "layoutSpecDigest",
                template.LayoutSpecDigest.Hex);
            writer.WriteNumber("width", template.Width);
            writer.WriteNumber("height", template.Height);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOutput(
        Utf8JsonWriter writer,
        ThumbnailBridgeOutput output)
    {
        ManualThumbnailResult result = output.Result;
        writer.WriteStartObject();
        writer.WriteString("generationId", result.RequestId.ToString());
        writer.WriteString("artifactId", result.ArtifactId.ToString());
        writer.WriteString("projectId", result.ProjectId.ToString());
        writer.WriteString("mediaReference", output.MediaReference.Value);
        writer.WriteString("bindingDigest", result.BindingDigest.Hex);
        writer.WriteString(
            "contentDigest",
            result.OutputContentDigest.Hex);
        writer.WriteString(
            "byteSize",
            result.OutputByteSize.ToString(CultureInfo.InvariantCulture));
        writer.WriteNumber("width", result.OutputWidth);
        writer.WriteNumber("height", result.OutputHeight);
        writer.WriteString("createdAt", FormatTimestamp(result.CreatedAt));
        writer.WriteBoolean(
            "wasAlreadyRegistered",
            result.WasAlreadyRegistered);
        writer.WriteStartObject("source");
        writer.WriteString(
            "artifactId",
            result.Source.SourceArtifactId.ToString());
        writer.WriteString("assetId", result.Source.SourceAssetId.ToString());
        writer.WriteString(
            "contentDigest",
            result.Source.SourceContentDigest.Hex);
        writer.WriteString(
            "ptsIndexDigest",
            result.Source.SourcePtsIndexDigest.Hex);
        writer.WriteString(
            "frameMapDigest",
            result.Source.ProxyFrameMapDigest.Hex);
        writer.WriteString(
            "rightsStatus",
            GetRightsStatusText(result.SourceRightsStatus));
        writer.WriteBoolean(
            "externalPublicationAllowed",
            result.SourceExternalPublicationAllowed);
        writer.WriteEndObject();
        writer.WriteStartObject("selection");
        writer.WriteNumber(
            "proxyFrameSequence",
            result.Selection.ProxyFrameSequence);
        writer.WriteNumber(
            "sourceFrameSequence",
            result.Selection.SourceFrameSequence);
        writer.WritePropertyName("sourcePresentationTime");
        WriteRational(
            writer,
            result.Selection.SourcePresentationTime ??
                throw new InvalidOperationException(
                    "A generated thumbnail must retain exact source PTS."));
        writer.WriteString(
            "selectedAt",
            FormatTimestamp(result.Selection.SelectedAt));
        writer.WriteEndObject();
        writer.WriteStartObject("template");
        writer.WriteString(
            "approvalId",
            result.Template.ApprovalId.ToString());
        writer.WriteString(
            "approvalDigest",
            result.Template.ApprovalDigest.Hex);
        writer.WriteString("approvalRole", "brandApprover");
        writer.WriteString(
            "approvedAt",
            FormatTimestamp(result.TemplateApprovedAt));
        writer.WriteString("templateId", result.Template.TemplateId.ToString());
        writer.WriteString(
            "templateVersionDigest",
            result.Template.TemplateVersionDigest.Hex);
        writer.WriteString(
            "renderProfileId",
            result.Template.RenderProfileId.ToString());
        writer.WriteString(
            "renderProfileDigest",
            result.Template.RenderProfileDigest.Hex);
        writer.WriteString(
            "layoutSpecId",
            result.Template.LayoutSpecId.ToString());
        writer.WriteString(
            "layoutSpecDigest",
            result.Template.LayoutSpecDigest.Hex);
        writer.WriteEndObject();
        writer.WriteStartObject("origin");
        writer.WriteString("kind", "asset");
        writer.WriteString(
            "assetId",
            result.Origin.AssetId?.ToString() ??
                throw new InvalidOperationException(
                    "A generated thumbnail must retain source provenance."));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteRational(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string GetRightsStatusText(RightsStatus status) =>
        status switch
        {
            RightsStatus.Approved => "approved",
            RightsStatus.Unconfirmed => "unconfirmed",
            RightsStatus.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown thumbnail rights status."),
        };

    private static string GetStatusText(ThumbnailBridgeStatus status) =>
        status switch
        {
            ThumbnailBridgeStatus.Available => "available",
            ThumbnailBridgeStatus.Forbidden => "forbidden",
            ThumbnailBridgeStatus.Unavailable => "unavailable",
            ThumbnailBridgeStatus.Stale => "stale",
            ThumbnailBridgeStatus.Mismatched => "mismatched",
            ThumbnailBridgeStatus.InvalidTime => "invalidTime",
            ThumbnailBridgeStatus.RightsNotApproved => "rightsNotApproved",
            ThumbnailBridgeStatus.TemplateUnavailable => "templateUnavailable",
            ThumbnailBridgeStatus.Cancelled => "cancelled",
            ThumbnailBridgeStatus.Conflict => "conflict",
            ThumbnailBridgeStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown thumbnail bridge status."),
        };

    private static string GetOutcomeText(ThumbnailCancellationOutcome outcome) =>
        outcome switch
        {
            ThumbnailCancellationOutcome.Requested => "requested",
            ThumbnailCancellationOutcome.AlreadyRequested => "alreadyRequested",
            ThumbnailCancellationOutcome.NotRunning => "notRunning",
            ThumbnailCancellationOutcome.NotFound => "notFound",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown thumbnail cancellation outcome."),
        };
}
