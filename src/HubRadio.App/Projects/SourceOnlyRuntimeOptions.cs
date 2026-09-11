using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Models;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Projects;

/// <summary>
/// Trusted native host configuration, NOT deserializable renderer input. Paths are resolved
/// by the host; the pipeline never searches PATH, the repository, or user profile directories.
/// ModelManager must refer to this workspace and the pinned, already installed offline model.
/// The database must already be migrated. No migration or model download is performed here.
/// </summary>
public sealed class SourceOnlyRuntimeOptions
{
    private readonly byte[] landscapeProfileJson;
    private readonly byte[] verticalProfileJson;

    public SourceOnlyRuntimeOptions(
        string databasePath,
        string workspacePath,
        string ffmpegPath,
        string ffprobePath,
        string pythonExecutable,
        string modelDirectory,
        string profilesDirectory,
        string defaultSettingsProfilePath,
        FfmpegFontBinding font,
        BinaryVerificationRequest pythonVerification,
        ModelManager modelManager,
        string applicationVersion,
        ContentDigest applicationDigest,
        TimeSpan? operationTimeout = null,
        string? musicDirectory = null)
    {
        DatabasePath = SourceOnlySourceLocator.RequirePath(databasePath, directory: false);
        WorkspacePath = SourceOnlySourceLocator.RequirePath(workspacePath, directory: true);
        FfmpegPath = SourceOnlySourceLocator.RequirePath(ffmpegPath, directory: false);
        FfprobePath = SourceOnlySourceLocator.RequirePath(ffprobePath, directory: false);
        PythonExecutable = SourceOnlySourceLocator.RequirePath(pythonExecutable, directory: false);
        ModelDirectory = SourceOnlySourceLocator.RequirePath(modelDirectory, directory: true);
        ProfilesDirectory = SourceOnlySourceLocator.RequirePath(profilesDirectory, directory: true);
        DefaultSettingsProfilePath = SourceOnlySourceLocator.RequirePath(defaultSettingsProfilePath, directory: false);
        MusicDirectory = ResolveMusicDirectory(ProfilesDirectory, musicDirectory);
        Font = font ?? throw new ArgumentNullException(nameof(font));
        _ = SourceOnlySourceLocator.RequirePath(font.VerificationRequest.FilePath, directory: false);
        PythonVerification = pythonVerification ?? throw new ArgumentNullException(nameof(pythonVerification));
        if (!string.Equals(PythonVerification.FilePath, PythonExecutable, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Python verification must bind the exact configured interpreter.", nameof(pythonVerification));
        }
        ModelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        if (applicationVersion.Length > 128 || applicationVersion != applicationVersion.Trim())
        {
            throw new ArgumentException("Invalid native application version.", nameof(applicationVersion));
        }
        ApplicationVersion = applicationVersion;
        ApplicationDigest = applicationDigest ?? throw new ArgumentNullException(nameof(applicationDigest));
        OperationTimeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        // Freeze the approved measured profiles, not mutable paths read later during a job.
        landscapeProfileJson = SourceOnlySourceLocator.ReadBoundedFile(
            Path.Combine(ProfilesDirectory, "16x9", "long-form.json"), BundledRenderProfileLoader.MaximumUtf8JsonBytes);
        verticalProfileJson = SourceOnlySourceLocator.ReadBoundedFile(
            Path.Combine(ProfilesDirectory, "9x16", "vertical-short.json"), BundledRenderProfileLoader.MaximumUtf8JsonBytes);
        _ = BundledRenderProfileLoader.Load(landscapeProfileJson);
        _ = BundledRenderProfileLoader.Load(verticalProfileJson);
        BundledSettingsProfile = BundledDefaultSettingsProfileLoader.Load(
            SourceOnlySourceLocator.ReadBoundedFile(DefaultSettingsProfilePath,
                DefaultSettingsStore.MaximumCanonicalJsonUtf8Bytes));
    }

    public string DatabasePath { get; }
    public string WorkspacePath { get; }
    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public string PythonExecutable { get; }
    public string ModelDirectory { get; }
    public string ProfilesDirectory { get; }
    public string DefaultSettingsProfilePath { get; }
    public string MusicDirectory { get; }
    public FfmpegFontBinding Font { get; }
    public BinaryVerificationRequest PythonVerification { get; }
    public ModelManager ModelManager { get; }
    public string ApplicationVersion { get; }
    public ContentDigest ApplicationDigest { get; }
    public TimeSpan OperationTimeout { get; }
    public DefaultSettingsProfile BundledSettingsProfile { get; }
    public ReadOnlyMemory<byte> LandscapeProfileJson => landscapeProfileJson.ToArray();
    public ReadOnlyMemory<byte> VerticalProfileJson => verticalProfileJson.ToArray();

    internal static string ResolveMusicDirectory(string profilesDirectory, string? musicDirectory)
    {
        string profiles = Path.TrimEndingDirectorySeparator(SourceOnlySourceLocator.RequirePath(profilesDirectory, directory: true));
        if (musicDirectory is null)
        {
            if (!string.Equals(Path.GetFileName(profiles), "Profiles", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("An explicit Music directory is required for a custom profile layout.", nameof(musicDirectory));
            }
            string root = Path.GetDirectoryName(profiles)!;
            string? parent = Path.GetDirectoryName(root);
            if (string.Equals(Path.GetFileName(root), "HubRadio.App", StringComparison.OrdinalIgnoreCase) && parent is not null &&
                string.Equals(Path.GetFileName(parent), "src", StringComparison.OrdinalIgnoreCase))
            {
                root = Path.GetDirectoryName(parent)!;
                _ = SourceOnlySourceLocator.RequirePath(Path.Combine(root, "HubRadio.slnx"), directory: false);
            }
            musicDirectory = Path.Combine(root, "Music");
        }
        musicDirectory = Path.TrimEndingDirectorySeparator(musicDirectory);
        if (!string.Equals(Path.GetFileName(musicDirectory), "Music", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The native music library must be a Music directory.", nameof(musicDirectory));
        }
        return SourceOnlySourceLocator.RequireOptionalDirectory(musicDirectory);
    }
}

/// <summary>
/// Native runtime registration hooks. Called and awaited on a background job, including
/// checkpoint reloads; registrations must be idempotent. Hosts dispatch to their UI thread
/// if necessary. These do not replace persistence and must not initiate another heavy job.
/// </summary>
public sealed class SourceOnlyRuntimeCallbacks
{
    public Func<IngestWorkflowRunResult, CancellationToken, Task>? IngestReady { get; init; }
    public Func<Project, TranscriptImportResult, CancellationToken, Task>? TranscriptReady { get; init; }
    public Func<Project, TranscriptImportResult, SourceOnlyAnalysisEvidence, CancellationToken, Task>? AnalysisReady { get; init; }
    public Func<StandardCompositionPreparation, TranscriptImportResult, CancellationToken, Task>? CompositionReady { get; init; }
    /// <summary>
    /// Awaited after the actual subtitle inputs and their initial histories are durably
    /// checkpointed, before rendering. Also called for validated checkpoint restoration.
    /// This is subtitle-generation evidence, NOT render completion, QC or approval.
    /// </summary>
    public Func<SourceOnlyGeneratedRuntimeEvidence, CancellationToken, Task>? GeneratedReady { get; init; }
}

/// <summary>
/// Exact inputs passed to the standard generator. Versions contains the landscape
/// version and EVERY planned short version, even when its actual cue set is empty.
/// The landscape cues serve clean/sidecar and burn-in outputs; no 9:16 substitute is
/// created for a landscape-only plan. Hosts can register Histories without inventing
/// IDs, timestamps, text or a Human transcript. Rendering outcomes are separate evidence.
/// </summary>
public sealed record SourceOnlyGeneratedRuntimeEvidence(
    StandardCompositionPreparation Preparation,
    TranscriptImportResult Import,
    StandardSubtitleBuildResult LandscapeSubtitles,
    IReadOnlyList<StandardShortArtifactInput> ShortForms,
    IReadOnlyDictionary<EntityId<CompositionVersionIdTag>, SourceOnlyGeneratedSubtitleRuntimeEvidence> Versions,
    DateTimeOffset CreatedAt,
    RenderFontEvidence Font);

/// <summary>Actual subtitle-builder profile, production timeline and initial cue histories for one exact version.</summary>
public sealed record SourceOnlyGeneratedSubtitleRuntimeEvidence(
    CompositionVersion Version,
    TimelineMap Timeline,
    LayoutSpec Layout,
    RenderProfile Profile,
    StandardSubtitleBuildResult Build,
    IReadOnlyList<SubtitleCueHistory> Histories)
{
    // Native capability, never populated from ordinary runtime/wire JSON. The
    // checkpoint codec must explicitly persist and rederive the canonical proof.
    internal SubtitleEditProjectionEvidence? SubtitleProjection { get; init; }
    internal SourceOnlySubtitleProjectionSource? SubtitleProjectionSource { get; init; }
}

/// <summary>Coordinator-derived provenance, rebuilt from the accepted parent and journal prefix on every read.</summary>
internal sealed record SourceOnlySubtitleProjectionSource(
    EntityId<SourceOnlyExecutionIdTag> ExecutionId,
    EntityId<CompositionVersionIdTag> RootVersionId,
    int CapturedSequence);

/// <summary>Actual producer results for native Analysis registration; no internal bridge types.</summary>
public sealed record SourceOnlyAnalysisEvidence(
    DateTimeOffset CreatedAt,
    TopicBoundaryScanResult TopicBoundaries,
    IReadOnlyList<TopicRangeRevision> TopicRanges,
    IReadOnlyList<TopicMetadataRevision> TopicMetadata,
    IReadOnlyList<EmphasisSuggestionCandidate> EmphasisCandidates,
    IReadOnlyList<EditorialPhraseSuggestionCandidate> EditorialCandidates,
    IReadOnlyList<string> Warnings);

// Kept internal with ProjectCreationService: existing callers that omit it retain the old
// draft-only semantics. The production host passes its owned SourceOnlyPipeline instance.
internal interface ISourceOnlyProjectCoordinator
{
    Task CaptureSelectionAsync(EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId, string path, string actorSid,
        DateTimeOffset selectedAt, CancellationToken cancellationToken);
    // Optional for legacy coordinators. Never obtains a replacement picker token.
    public bool RestoreSelection(EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId) => false;
    void ForgetSelection(EntityId<ProjectCreationDraftIdTag> draftId);
    StoredProjectCreationDraft Accept(ProjectCreationDraft draft, DefaultSettingsSnapshot settings,
        string payloadJson, int expectedRevision, string actorSid, DateTimeOffset acceptedAt);
}