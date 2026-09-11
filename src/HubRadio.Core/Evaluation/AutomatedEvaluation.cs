using System.Text;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;

namespace HubRadio.Core.Evaluation;

public sealed class AutomatedEvaluationIdTag
{
}

public sealed class AutomatedEvaluationArtifactIdTag
{
}

public enum AutomatedEvaluationRuleId
{
    Codec,
    FullDecode,
    Duration,
    AvSync,
    SubtitlePresence,
    SubtitleReadingSpeed,
    SubtitleLineLength,
    SubtitleLineCount,
    SubtitleTiming,
    SubtitleSafeZone,
    Loudness,
    TruePeak,
    Provenance,
    SettingsBinding,
    Keyboard,
    Focus,
    Contrast,
    AccessibilityTree,
    SubtitleSemanticCompleteness,
    NonSpeechSoundCoverage,
    SpeakerInformationCoverage,
    MainContentUnderstanding,
    HumanReadability,
    Naturalness,
    PerceivedAudioQuality,
    EmotionalFit,
    LegalCompliance,
}

public enum AutomatedEvaluationRuleCategory
{
    Machine,
    HumanLimitation,
}

public enum AutomatedEvaluationRuleScope
{
    Artifact,
    UserInterface,
    HumanReview,
}

public enum AutomatedEvaluationStatus
{
    Pass,
    Warning,
    Fail,
    NotEvaluated,
}

public enum AutomatedEvaluationTargetKind
{
    Preview,
    Settings,
    Evidence,
    Editor,
}

public enum AutomatedEvaluationEditorKind
{
    Timeline,
    Subtitles,
    Audio,
    Layout,
    Metadata,
}

public enum AutomatedEvaluationTimeBasis
{
    Source,
    Output,
}

public sealed class AutomatedEvaluationRuleDefinition
{
    internal AutomatedEvaluationRuleDefinition(
        AutomatedEvaluationRuleId ruleId,
        AutomatedEvaluationRuleCategory category,
        AutomatedEvaluationRuleScope scope)
    {
        RuleId = ruleId;
        Category = category;
        Scope = scope;
    }

    public AutomatedEvaluationRuleId RuleId { get; }

    public AutomatedEvaluationRuleCategory Category { get; }

    public AutomatedEvaluationRuleScope Scope { get; }
}

public static class AutomatedEvaluationRules
{
    private static readonly AutomatedEvaluationRuleDefinition[] RuleDefinitions =
    [
        ArtifactRule(AutomatedEvaluationRuleId.Codec),
        ArtifactRule(AutomatedEvaluationRuleId.FullDecode),
        ArtifactRule(AutomatedEvaluationRuleId.Duration),
        ArtifactRule(AutomatedEvaluationRuleId.AvSync),
        ArtifactRule(AutomatedEvaluationRuleId.SubtitlePresence),
        ArtifactRule(AutomatedEvaluationRuleId.SubtitleReadingSpeed),
        ArtifactRule(AutomatedEvaluationRuleId.SubtitleLineLength),
        ArtifactRule(AutomatedEvaluationRuleId.SubtitleLineCount),
        ArtifactRule(AutomatedEvaluationRuleId.SubtitleTiming),
        ArtifactRule(AutomatedEvaluationRuleId.SubtitleSafeZone),
        ArtifactRule(AutomatedEvaluationRuleId.Loudness),
        ArtifactRule(AutomatedEvaluationRuleId.TruePeak),
        ArtifactRule(AutomatedEvaluationRuleId.Provenance),
        ArtifactRule(AutomatedEvaluationRuleId.SettingsBinding),
        UserInterfaceRule(AutomatedEvaluationRuleId.Keyboard),
        UserInterfaceRule(AutomatedEvaluationRuleId.Focus),
        UserInterfaceRule(AutomatedEvaluationRuleId.Contrast),
        UserInterfaceRule(AutomatedEvaluationRuleId.AccessibilityTree),
        HumanLimitation(AutomatedEvaluationRuleId.SubtitleSemanticCompleteness),
        HumanLimitation(AutomatedEvaluationRuleId.NonSpeechSoundCoverage),
        HumanLimitation(AutomatedEvaluationRuleId.SpeakerInformationCoverage),
        HumanLimitation(AutomatedEvaluationRuleId.MainContentUnderstanding),
        HumanLimitation(AutomatedEvaluationRuleId.HumanReadability),
        HumanLimitation(AutomatedEvaluationRuleId.Naturalness),
        HumanLimitation(AutomatedEvaluationRuleId.PerceivedAudioQuality),
        HumanLimitation(AutomatedEvaluationRuleId.EmotionalFit),
        HumanLimitation(AutomatedEvaluationRuleId.LegalCompliance),
    ];

    private static readonly IReadOnlyDictionary<AutomatedEvaluationRuleId, AutomatedEvaluationRuleDefinition>
        DefinitionsById = RuleDefinitions.ToDictionary(static definition => definition.RuleId);

    private static readonly AutomatedEvaluationRuleId[] RuleIds =
        RuleDefinitions.Select(static definition => definition.RuleId).ToArray();

    private static readonly AutomatedEvaluationRuleId[] MachineRuleIds =
        RuleDefinitions
            .Where(static definition =>
                definition.Category == AutomatedEvaluationRuleCategory.Machine)
            .Select(static definition => definition.RuleId)
            .ToArray();

    private static readonly AutomatedEvaluationRuleId[] HumanLimitationRuleIds =
        RuleDefinitions
            .Where(static definition =>
                definition.Category == AutomatedEvaluationRuleCategory.HumanLimitation)
            .Select(static definition => definition.RuleId)
            .ToArray();

    public static IReadOnlyList<AutomatedEvaluationRuleDefinition> Definitions { get; } =
        Array.AsReadOnly(RuleDefinitions);

    public static IReadOnlyList<AutomatedEvaluationRuleId> All { get; } =
        Array.AsReadOnly(RuleIds);

    public static IReadOnlyList<AutomatedEvaluationRuleId> Machine { get; } =
        Array.AsReadOnly(MachineRuleIds);

    public static IReadOnlyList<AutomatedEvaluationRuleId> HumanLimitations { get; } =
        Array.AsReadOnly(HumanLimitationRuleIds);

    public static AutomatedEvaluationRuleDefinition GetDefinition(
        AutomatedEvaluationRuleId ruleId)
    {
        AutomatedEvaluationContract.ValidateEnum(ruleId, nameof(ruleId));
        return DefinitionsById.TryGetValue(ruleId, out AutomatedEvaluationRuleDefinition? definition)
            ? definition
            : throw new ArgumentOutOfRangeException(
                nameof(ruleId),
                ruleId,
                "Unknown evaluation rule.");
    }

    public static string ToWireName(AutomatedEvaluationRuleId ruleId) =>
        AutomatedEvaluationContract.ToWireName(ruleId);

    private static AutomatedEvaluationRuleDefinition ArtifactRule(
        AutomatedEvaluationRuleId ruleId) =>
        new(
            ruleId,
            AutomatedEvaluationRuleCategory.Machine,
            AutomatedEvaluationRuleScope.Artifact);

    private static AutomatedEvaluationRuleDefinition UserInterfaceRule(
        AutomatedEvaluationRuleId ruleId) =>
        new(
            ruleId,
            AutomatedEvaluationRuleCategory.Machine,
            AutomatedEvaluationRuleScope.UserInterface);

    private static AutomatedEvaluationRuleDefinition HumanLimitation(
        AutomatedEvaluationRuleId ruleId) =>
        new(
            ruleId,
            AutomatedEvaluationRuleCategory.HumanLimitation,
            AutomatedEvaluationRuleScope.HumanReview);
}

public sealed class AutomatedEvaluationTarget
{
    private AutomatedEvaluationTarget(
        AutomatedEvaluationTargetKind kind,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId,
        ContentDigest? artifactDigest,
        AutomatedEvaluationTimeBasis? timeBasis,
        RationalTime? time,
        string? settingKey,
        AutomatedEvaluationEditorKind? editorKind)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);
        AutomatedEvaluationContract.ValidateEnum(kind, nameof(kind));
        AutomatedEvaluationContract.ValidateOptionalArtifactBinding(
            artifactId,
            artifactDigest,
            nameof(artifactId),
            nameof(artifactDigest));

        Kind = kind;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        ArtifactId = artifactId;
        ArtifactDigest = artifactDigest;
        TimeBasis = timeBasis;
        Time = time;
        SettingKey = settingKey;
        EditorKind = editorKind;
    }

    public AutomatedEvaluationTargetKind Kind { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<AutomatedEvaluationArtifactIdTag>? ArtifactId { get; }

    public ContentDigest? ArtifactDigest { get; }

    public AutomatedEvaluationTimeBasis? TimeBasis { get; }

    public RationalTime? Time { get; }

    public string? SettingKey { get; }

    public AutomatedEvaluationEditorKind? EditorKind { get; }

    public static AutomatedEvaluationTarget Preview(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        AutomatedEvaluationTimeBasis timeBasis,
        RationalTime time,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null,
        ContentDigest? artifactDigest = null)
    {
        AutomatedEvaluationContract.ValidateEnum(timeBasis, nameof(timeBasis));
        if (time.Value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(time),
                "A preview time must not be negative.");
        }

        return new AutomatedEvaluationTarget(
            AutomatedEvaluationTargetKind.Preview,
            projectId,
            compositionVersionId,
            compositionVersionDigest,
            artifactId,
            artifactDigest,
            timeBasis,
            time,
            settingKey: null,
            editorKind: null);
    }

    public static AutomatedEvaluationTarget Settings(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        string settingKey,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null,
        ContentDigest? artifactDigest = null)
    {
        AutomatedEvaluationContract.ValidateSettingKey(settingKey, nameof(settingKey));
        return new AutomatedEvaluationTarget(
            AutomatedEvaluationTargetKind.Settings,
            projectId,
            compositionVersionId,
            compositionVersionDigest,
            artifactId,
            artifactDigest,
            timeBasis: null,
            time: null,
            settingKey,
            editorKind: null);
    }

    public static AutomatedEvaluationTarget Evidence(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null,
        ContentDigest? artifactDigest = null) =>
        new(
            AutomatedEvaluationTargetKind.Evidence,
            projectId,
            compositionVersionId,
            compositionVersionDigest,
            artifactId,
            artifactDigest,
            timeBasis: null,
            time: null,
            settingKey: null,
            editorKind: null);

    public static AutomatedEvaluationTarget Editor(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        AutomatedEvaluationEditorKind editorKind,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null,
        ContentDigest? artifactDigest = null)
    {
        AutomatedEvaluationContract.ValidateEnum(editorKind, nameof(editorKind));
        return new AutomatedEvaluationTarget(
            AutomatedEvaluationTargetKind.Editor,
            projectId,
            compositionVersionId,
            compositionVersionDigest,
            artifactId,
            artifactDigest,
            timeBasis: null,
            time: null,
            settingKey: null,
            editorKind);
    }
}

public sealed class AutomatedEvaluationRuleObservation
{
    private AutomatedEvaluationRuleObservation(
        AutomatedEvaluationRuleId ruleId,
        AutomatedEvaluationStatus status,
        string reasonCode,
        ContentDigest? evidenceDigest,
        AutomatedEvaluationTarget? target)
    {
        AutomatedEvaluationRuleDefinition definition =
            AutomatedEvaluationRules.GetDefinition(ruleId);
        if (definition.Category != AutomatedEvaluationRuleCategory.Machine)
        {
            throw new ArgumentException(
                "Human-review limitations cannot be supplied as automated observations.",
                nameof(ruleId));
        }

        AutomatedEvaluationContract.ValidateEnum(status, nameof(status));
        AutomatedEvaluationContract.ValidateReasonCode(reasonCode, nameof(reasonCode));

        if (status == AutomatedEvaluationStatus.NotEvaluated)
        {
            if (evidenceDigest is not null || target is not null)
            {
                throw new ArgumentException(
                    "A not-evaluated observation cannot contain evidence or target data.",
                    nameof(evidenceDigest));
            }
        }
        else
        {
            if (evidenceDigest is null)
            {
                throw new ArgumentException(
                    "A pass, warning, or fail observation requires an evidence digest.",
                    nameof(evidenceDigest));
            }

            if (status == AutomatedEvaluationStatus.Pass && target is not null)
            {
                throw new ArgumentException(
                    "A passing observation cannot contain a target.",
                    nameof(target));
            }
        }

        RuleId = ruleId;
        Status = status;
        ReasonCode = reasonCode;
        EvidenceDigest = evidenceDigest;
        Target = target;
    }

    public AutomatedEvaluationRuleId RuleId { get; }

    public AutomatedEvaluationStatus Status { get; }

    public string ReasonCode { get; }

    public ContentDigest? EvidenceDigest { get; }

    public AutomatedEvaluationTarget? Target { get; }

    public static AutomatedEvaluationRuleObservation ObjectivePass(
        AutomatedEvaluationRuleId ruleId,
        string reasonCode,
        ContentDigest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        return new(
            ruleId,
            AutomatedEvaluationStatus.Pass,
            reasonCode,
            evidenceDigest,
            target: null);
    }

    public static AutomatedEvaluationRuleObservation ObjectiveWarning(
        AutomatedEvaluationRuleId ruleId,
        string reasonCode,
        ContentDigest evidenceDigest,
        AutomatedEvaluationTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        return new(
            ruleId,
            AutomatedEvaluationStatus.Warning,
            reasonCode,
            evidenceDigest,
            target);
    }

    public static AutomatedEvaluationRuleObservation ObjectiveFail(
        AutomatedEvaluationRuleId ruleId,
        string reasonCode,
        ContentDigest evidenceDigest,
        AutomatedEvaluationTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        return new(
            ruleId,
            AutomatedEvaluationStatus.Fail,
            reasonCode,
            evidenceDigest,
            target);
    }

    public static AutomatedEvaluationRuleObservation NotEvaluated(
        AutomatedEvaluationRuleId ruleId,
        string reasonCode) =>
        new(
            ruleId,
            AutomatedEvaluationStatus.NotEvaluated,
            reasonCode,
            evidenceDigest: null,
            target: null);
}

public sealed class AutomatedEvaluationRequest
{
    public AutomatedEvaluationRequest(
        EntityId<AutomatedEvaluationIdTag> evaluationId,
        EntityId<JobIdTag>? jobId,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId,
        ContentDigest? artifactDigest,
        EntityId<DefaultSettingsProfileIdTag> settingsProfileId,
        SettingsProfileVersion settingsProfileVersion,
        ContentDigest settingsProfileDigest,
        ContentDigest settingsSnapshotDigest,
        SettingsProfileVersion ruleSetVersion,
        string environmentVersion,
        ContentDigest environmentDigest,
        ContentDigest toolVersionsDigest,
        ContentDigest? modelVersionsDigest,
        ContentDigest? templateDigest,
        ContentDigest? musicDigest,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        DateTimeOffset evaluatedAt,
        ContentDigest inputDigest,
        ContentDigest? outputDigest,
        IReadOnlyList<AutomatedEvaluationRuleObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(evaluationId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);
        ArgumentNullException.ThrowIfNull(settingsProfileId);
        ArgumentNullException.ThrowIfNull(settingsProfileVersion);
        ArgumentNullException.ThrowIfNull(settingsProfileDigest);
        ArgumentNullException.ThrowIfNull(settingsSnapshotDigest);
        ArgumentNullException.ThrowIfNull(ruleSetVersion);
        ArgumentNullException.ThrowIfNull(environmentDigest);
        ArgumentNullException.ThrowIfNull(toolVersionsDigest);
        ArgumentNullException.ThrowIfNull(inputDigest);
        ArgumentNullException.ThrowIfNull(observations);

        AutomatedEvaluationContract.ValidateOptionalArtifactBinding(
            artifactId,
            artifactDigest,
            nameof(artifactId),
            nameof(artifactDigest));
        if ((artifactId is null) != (outputDigest is null))
        {
            throw new ArgumentException(
                "An output digest must be present exactly when an artifact binding is present.",
                nameof(outputDigest));
        }

        if (artifactDigest != outputDigest)
        {
            throw new ArgumentException(
                "The output digest must equal the bound rendered-artifact digest.",
                nameof(outputDigest));
        }

        AutomatedEvaluationContract.ValidateEnvironmentVersion(
            environmentVersion,
            nameof(environmentVersion));
        AutomatedEvaluationContract.ValidateUtcTimestamp(startedAt, nameof(startedAt));
        AutomatedEvaluationContract.ValidateUtcTimestamp(completedAt, nameof(completedAt));
        AutomatedEvaluationContract.ValidateUtcTimestamp(evaluatedAt, nameof(evaluatedAt));
        if (completedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "The evaluation completion timestamp must not precede its start timestamp.");
        }

        if (evaluatedAt < completedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evaluatedAt),
                "The evaluated timestamp must not precede the completion timestamp.");
        }

        var copiedObservations = new AutomatedEvaluationRuleObservation[observations.Count];
        HashSet<AutomatedEvaluationRuleId> observedRules = [];
        for (int index = 0; index < copiedObservations.Length; index++)
        {
            AutomatedEvaluationRuleObservation? observation = observations[index];
            if (observation is null)
            {
                throw new ArgumentException(
                    "Evaluation observations must not contain null elements.",
                    nameof(observations));
            }

            if (!observedRules.Add(observation.RuleId))
            {
                throw new ArgumentException(
                    "Evaluation observations must contain at most one observation per rule.",
                    nameof(observations));
            }

            if (AutomatedEvaluationRules.GetDefinition(observation.RuleId).Category !=
                AutomatedEvaluationRuleCategory.Machine)
            {
                throw new ArgumentException(
                    "Human-review limitations cannot be supplied as automated observations.",
                    nameof(observations));
            }

            copiedObservations[index] = observation;
        }

        EvaluationId = evaluationId;
        JobId = jobId;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        ArtifactId = artifactId;
        ArtifactDigest = artifactDigest;
        SettingsProfileId = settingsProfileId;
        SettingsProfileVersion = settingsProfileVersion;
        SettingsProfileDigest = settingsProfileDigest;
        SettingsSnapshotDigest = settingsSnapshotDigest;
        RuleSetVersion = ruleSetVersion;
        EnvironmentVersion = environmentVersion;
        EnvironmentDigest = environmentDigest;
        ToolVersionsDigest = toolVersionsDigest;
        ModelVersionsDigest = modelVersionsDigest;
        TemplateDigest = templateDigest;
        MusicDigest = musicDigest;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        EvaluatedAt = evaluatedAt;
        InputDigest = inputDigest;
        OutputDigest = outputDigest;
        Observations = Array.AsReadOnly(copiedObservations);
    }

    public EntityId<AutomatedEvaluationIdTag> EvaluationId { get; }

    public EntityId<JobIdTag>? JobId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<AutomatedEvaluationArtifactIdTag>? ArtifactId { get; }

    public ContentDigest? ArtifactDigest { get; }

    public EntityId<DefaultSettingsProfileIdTag> SettingsProfileId { get; }

    public SettingsProfileVersion SettingsProfileVersion { get; }

    public ContentDigest SettingsProfileDigest { get; }

    public ContentDigest SettingsSnapshotDigest { get; }

    public SettingsProfileVersion RuleSetVersion { get; }

    public string EnvironmentVersion { get; }

    public ContentDigest EnvironmentDigest { get; }

    public ContentDigest ToolVersionsDigest { get; }

    public ContentDigest? ModelVersionsDigest { get; }

    public ContentDigest? TemplateDigest { get; }

    public ContentDigest? MusicDigest { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public DateTimeOffset EvaluatedAt { get; }

    public ContentDigest InputDigest { get; }

    public ContentDigest? OutputDigest { get; }

    public IReadOnlyList<AutomatedEvaluationRuleObservation> Observations { get; }
}

public sealed class AutomatedEvaluationRuleResult
{
    internal AutomatedEvaluationRuleResult(AutomatedEvaluationRuleObservation observation)
        : this(
            observation.RuleId,
            observation.Status,
            observation.ReasonCode,
            observation.EvidenceDigest,
            observation.Target)
    {
    }

    internal AutomatedEvaluationRuleResult(
        AutomatedEvaluationRuleId ruleId,
        AutomatedEvaluationStatus status,
        string reasonCode,
        ContentDigest? evidenceDigest,
        AutomatedEvaluationTarget? target)
    {
        RuleId = ruleId;
        Status = status;
        ReasonCode = reasonCode;
        EvidenceDigest = evidenceDigest;
        Target = target;
    }

    public AutomatedEvaluationRuleId RuleId { get; }

    public AutomatedEvaluationRuleCategory Category =>
        AutomatedEvaluationRules.GetDefinition(RuleId).Category;

    public AutomatedEvaluationRuleScope Scope =>
        AutomatedEvaluationRules.GetDefinition(RuleId).Scope;

    public AutomatedEvaluationStatus Status { get; }

    public string ReasonCode { get; }

    public ContentDigest? EvidenceDigest { get; }

    public AutomatedEvaluationTarget? Target { get; }
}

public sealed class AutomatedEvaluationStatusCounts
{
    internal AutomatedEvaluationStatusCounts(
        IEnumerable<AutomatedEvaluationRuleResult> results)
    {
        int pass = 0;
        int warning = 0;
        int fail = 0;
        int notEvaluated = 0;
        foreach (AutomatedEvaluationRuleResult result in results)
        {
            switch (result.Status)
            {
                case AutomatedEvaluationStatus.Pass:
                    pass++;
                    break;
                case AutomatedEvaluationStatus.Warning:
                    warning++;
                    break;
                case AutomatedEvaluationStatus.Fail:
                    fail++;
                    break;
                case AutomatedEvaluationStatus.NotEvaluated:
                    notEvaluated++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(results),
                        result.Status,
                        "Unknown evaluation status.");
            }
        }

        Pass = pass;
        Warning = warning;
        Fail = fail;
        NotEvaluated = notEvaluated;
    }

    public int Pass { get; }

    public int Warning { get; }

    public int Fail { get; }

    public int NotEvaluated { get; }

    public int Total => Pass + Warning + Fail + NotEvaluated;
}

public sealed class AutomatedEvaluationRun
{
    public const int MaximumCanonicalJsonUtf8Bytes = 256 * 1024;

    internal AutomatedEvaluationRun(
        AutomatedEvaluationRequest request,
        AutomatedEvaluationStatus aggregateStatus,
        IReadOnlyList<AutomatedEvaluationRuleResult> rules,
        IReadOnlyList<AutomatedEvaluationRuleResult> limitations)
    {
        EvaluationId = request.EvaluationId;
        JobId = request.JobId;
        ProjectId = request.ProjectId;
        CompositionVersionId = request.CompositionVersionId;
        CompositionVersionDigest = request.CompositionVersionDigest;
        ArtifactId = request.ArtifactId;
        ArtifactDigest = request.ArtifactDigest;
        SettingsProfileId = request.SettingsProfileId;
        SettingsProfileVersion = request.SettingsProfileVersion;
        SettingsProfileDigest = request.SettingsProfileDigest;
        SettingsSnapshotDigest = request.SettingsSnapshotDigest;
        RuleSetVersion = request.RuleSetVersion;
        EnvironmentVersion = request.EnvironmentVersion;
        EnvironmentDigest = request.EnvironmentDigest;
        ToolVersionsDigest = request.ToolVersionsDigest;
        ModelVersionsDigest = request.ModelVersionsDigest;
        TemplateDigest = request.TemplateDigest;
        MusicDigest = request.MusicDigest;
        StartedAt = request.StartedAt;
        CompletedAt = request.CompletedAt;
        EvaluatedAt = request.EvaluatedAt;
        InputDigest = request.InputDigest;
        OutputDigest = request.OutputDigest;
        AggregateStatus = aggregateStatus;
        Rules = Array.AsReadOnly(rules.ToArray());
        Limitations = Array.AsReadOnly(limitations.ToArray());
        StatusCounts = new AutomatedEvaluationStatusCounts(Rules.Concat(Limitations));
        CanonicalJson = AutomatedEvaluationJsonCodec.Serialize(this);
        byte[] canonicalUtf8 = AutomatedEvaluationContract.StrictUtf8.GetBytes(CanonicalJson);
        if (canonicalUtf8.Length > MaximumCanonicalJsonUtf8Bytes)
        {
            throw new ArgumentException(
                "The automated-evaluation canonical evidence exceeds the supported byte limit.",
                nameof(rules));
        }

        CanonicalDigest = ContentDigest.Compute(canonicalUtf8);
    }

    public EntityId<AutomatedEvaluationIdTag> EvaluationId { get; }

    public EntityId<JobIdTag>? JobId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<AutomatedEvaluationArtifactIdTag>? ArtifactId { get; }

    public ContentDigest? ArtifactDigest { get; }

    public EntityId<DefaultSettingsProfileIdTag> SettingsProfileId { get; }

    public SettingsProfileVersion SettingsProfileVersion { get; }

    public ContentDigest SettingsProfileDigest { get; }

    public ContentDigest SettingsSnapshotDigest { get; }

    public SettingsProfileVersion RuleSetVersion { get; }

    public string EnvironmentVersion { get; }

    public ContentDigest EnvironmentDigest { get; }

    public ContentDigest ToolVersionsDigest { get; }

    public ContentDigest? ModelVersionsDigest { get; }

    public ContentDigest? TemplateDigest { get; }

    public ContentDigest? MusicDigest { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public DateTimeOffset EvaluatedAt { get; }

    public ContentDigest InputDigest { get; }

    public ContentDigest? OutputDigest { get; }

    public AutomatedEvaluationStatus AggregateStatus { get; }

    public IReadOnlyList<AutomatedEvaluationRuleResult> Rules { get; }

    public IReadOnlyList<AutomatedEvaluationRuleResult> Limitations { get; }

    public AutomatedEvaluationStatusCounts StatusCounts { get; }

    public string CanonicalJson { get; }

    public ContentDigest CanonicalDigest { get; }

    public static AutomatedEvaluationRun ParseCanonical(string canonicalJson) =>
        AutomatedEvaluationJsonCodec.ParseCanonical(canonicalJson);
}

public sealed class AutomatedEvaluationEngine
{
    public const string MissingObservationReasonCode = "evaluation.observation_missing";
    public const string ArtifactUnavailableReasonCode = "evaluation.artifact_unavailable";
    public const string HumanReviewRequiredReasonCode = "evaluation.human_review_required";

    public AutomatedEvaluationRun Evaluate(AutomatedEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Dictionary<AutomatedEvaluationRuleId, AutomatedEvaluationRuleObservation> byRule =
            request.Observations.ToDictionary(static observation => observation.RuleId);
        var results = new AutomatedEvaluationRuleResult[AutomatedEvaluationRules.Machine.Count];
        for (int index = 0; index < AutomatedEvaluationRules.Machine.Count; index++)
        {
            AutomatedEvaluationRuleId ruleId = AutomatedEvaluationRules.Machine[index];
            AutomatedEvaluationRuleDefinition definition =
                AutomatedEvaluationRules.GetDefinition(ruleId);
            bool wasSupplied = byRule.TryGetValue(
                ruleId,
                out AutomatedEvaluationRuleObservation? supplied);
            if (wasSupplied &&
                definition.Scope == AutomatedEvaluationRuleScope.Artifact &&
                request.ArtifactId is null)
            {
                throw new ArgumentException(
                    "An artifact-scoped observation requires a bound output artifact.",
                    nameof(request));
            }

            AutomatedEvaluationRuleObservation observation = wasSupplied
                ? supplied!
                : AutomatedEvaluationRuleObservation.NotEvaluated(
                    ruleId,
                    definition.Scope == AutomatedEvaluationRuleScope.Artifact &&
                    request.ArtifactId is null
                        ? ArtifactUnavailableReasonCode
                        : MissingObservationReasonCode);
            ValidateTarget(request, observation);
            results[index] = new AutomatedEvaluationRuleResult(observation);
        }

        var limitations = new AutomatedEvaluationRuleResult[
            AutomatedEvaluationRules.HumanLimitations.Count];
        for (int index = 0; index < limitations.Length; index++)
        {
            limitations[index] = new AutomatedEvaluationRuleResult(
                AutomatedEvaluationRules.HumanLimitations[index],
                AutomatedEvaluationStatus.NotEvaluated,
                HumanReviewRequiredReasonCode,
                evidenceDigest: null,
                target: null);
        }

        AutomatedEvaluationStatus aggregateStatus = DeriveAggregateStatus(results);
        return new AutomatedEvaluationRun(
            request,
            aggregateStatus,
            Array.AsReadOnly(results),
            Array.AsReadOnly(limitations));
    }

    private static AutomatedEvaluationStatus DeriveAggregateStatus(
        IReadOnlyList<AutomatedEvaluationRuleResult> results)
    {
        if (results.Any(static result => result.Status == AutomatedEvaluationStatus.Fail))
        {
            return AutomatedEvaluationStatus.Fail;
        }

        if (results.Any(static result =>
                result.Status == AutomatedEvaluationStatus.NotEvaluated))
        {
            return AutomatedEvaluationStatus.NotEvaluated;
        }

        if (results.Any(static result => result.Status == AutomatedEvaluationStatus.Warning))
        {
            return AutomatedEvaluationStatus.Warning;
        }

        return AutomatedEvaluationStatus.Pass;
    }

    private static void ValidateTarget(
        AutomatedEvaluationRequest request,
        AutomatedEvaluationRuleObservation observation)
    {
        AutomatedEvaluationTarget? target = observation.Target;
        if (target is null)
        {
            return;
        }

        if (target.ProjectId != request.ProjectId ||
            target.CompositionVersionId != request.CompositionVersionId ||
            target.CompositionVersionDigest != request.CompositionVersionDigest)
        {
            throw new ArgumentException(
                "An evaluation target must bind to the evaluation project and composition version.",
                nameof(request));
        }

        if (target.ArtifactId != request.ArtifactId ||
            target.ArtifactDigest != request.ArtifactDigest)
        {
            throw new ArgumentException(
                "An evaluation target artifact must match the evaluation artifact binding.",
                nameof(request));
        }
    }
}

internal static class AutomatedEvaluationContract
{
    internal const string SchemaVersion = "1.0.0";
    internal const int MaximumEnvironmentVersionLength = 256;
    internal const int MaximumReasonCodeLength = 128;
    internal const int MaximumSettingKeyLength = 128;

    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Unknown evaluation value.");
        }
    }

    internal static void ValidateOptionalArtifactBinding(
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId,
        ContentDigest? artifactDigest,
        string artifactIdParameterName,
        string artifactDigestParameterName)
    {
        if ((artifactId is null) != (artifactDigest is null))
        {
            throw new ArgumentException(
                "Artifact ID and digest must either both be present or both be absent.",
                artifactId is null ? artifactIdParameterName : artifactDigestParameterName);
        }
    }

    internal static void ValidateEnvironmentVersion(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 ||
            value.Length > MaximumEnvironmentVersionLength ||
            string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\0'))
        {
            throw new ArgumentException(
                "An environment version must be bounded nonblank text without surrounding whitespace.",
                parameterName);
        }

        ValidateUnicode(value, parameterName, "An environment version");
    }

    internal static void ValidateReasonCode(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > MaximumReasonCodeLength)
        {
            throw new ArgumentException(
                "A reason code must use canonical lowercase dot-separated segments.",
                parameterName);
        }

        string[] segments = value.Split('.', StringSplitOptions.None);
        if (segments.Length < 2 || segments.Any(static segment => !IsCanonicalSegment(segment)))
        {
            throw new ArgumentException(
                "A reason code must use canonical lowercase dot-separated segments.",
                parameterName);
        }
    }

    internal static void ValidateSettingKey(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > MaximumSettingKeyLength)
        {
            throw new ArgumentException(
                "A setting key must contain between 1 and 128 ASCII characters.",
                parameterName);
        }

        bool atSegmentStart = true;
        foreach (char character in value)
        {
            if (atSegmentStart)
            {
                if (character is < 'a' or > 'z')
                {
                    throw new ArgumentException(
                        "Every setting-key segment must start with a lowercase ASCII letter.",
                        parameterName);
                }

                atSegmentStart = false;
                continue;
            }

            if (character == '.')
            {
                atSegmentStart = true;
            }
            else if (character is not (>= 'a' and <= 'z') &&
                     character is not (>= 'A' and <= 'Z') &&
                     character is not (>= '0' and <= '9'))
            {
                throw new ArgumentException(
                    "A setting key may contain only ASCII letters, digits, and segment dots.",
                    parameterName);
            }
        }

        if (atSegmentStart)
        {
            throw new ArgumentException(
                "A setting key must not end with a segment dot.",
                parameterName);
        }
    }

    internal static void ValidateUtcTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An evaluation timestamp must use an explicit UTC offset.",
                parameterName);
        }
    }

    internal static string ToWireName(AutomatedEvaluationRuleId value) => value switch
    {
        AutomatedEvaluationRuleId.Codec => "codec",
        AutomatedEvaluationRuleId.FullDecode => "fullDecode",
        AutomatedEvaluationRuleId.Duration => "duration",
        AutomatedEvaluationRuleId.AvSync => "avSync",
        AutomatedEvaluationRuleId.SubtitlePresence => "subtitlePresence",
        AutomatedEvaluationRuleId.SubtitleReadingSpeed => "subtitleReadingSpeed",
        AutomatedEvaluationRuleId.SubtitleLineLength => "subtitleLineLength",
        AutomatedEvaluationRuleId.SubtitleLineCount => "subtitleLineCount",
        AutomatedEvaluationRuleId.SubtitleTiming => "subtitleTiming",
        AutomatedEvaluationRuleId.SubtitleSafeZone => "subtitleSafeZone",
        AutomatedEvaluationRuleId.Loudness => "loudness",
        AutomatedEvaluationRuleId.TruePeak => "truePeak",
        AutomatedEvaluationRuleId.Provenance => "provenance",
        AutomatedEvaluationRuleId.SettingsBinding => "settingsBinding",
        AutomatedEvaluationRuleId.Keyboard => "keyboard",
        AutomatedEvaluationRuleId.Focus => "focus",
        AutomatedEvaluationRuleId.Contrast => "contrast",
        AutomatedEvaluationRuleId.AccessibilityTree => "accessibilityTree",
        AutomatedEvaluationRuleId.SubtitleSemanticCompleteness =>
            "subtitleSemanticCompleteness",
        AutomatedEvaluationRuleId.NonSpeechSoundCoverage => "nonSpeechSoundCoverage",
        AutomatedEvaluationRuleId.SpeakerInformationCoverage =>
            "speakerInformationCoverage",
        AutomatedEvaluationRuleId.MainContentUnderstanding => "mainContentUnderstanding",
        AutomatedEvaluationRuleId.HumanReadability => "humanReadability",
        AutomatedEvaluationRuleId.Naturalness => "naturalness",
        AutomatedEvaluationRuleId.PerceivedAudioQuality => "perceivedAudioQuality",
        AutomatedEvaluationRuleId.EmotionalFit => "emotionalFit",
        AutomatedEvaluationRuleId.LegalCompliance => "legalCompliance",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown evaluation rule."),
    };

    internal static AutomatedEvaluationRuleId ParseRuleId(string value) => value switch
    {
        "codec" => AutomatedEvaluationRuleId.Codec,
        "fullDecode" => AutomatedEvaluationRuleId.FullDecode,
        "duration" => AutomatedEvaluationRuleId.Duration,
        "avSync" => AutomatedEvaluationRuleId.AvSync,
        "subtitlePresence" => AutomatedEvaluationRuleId.SubtitlePresence,
        "subtitleReadingSpeed" => AutomatedEvaluationRuleId.SubtitleReadingSpeed,
        "subtitleLineLength" => AutomatedEvaluationRuleId.SubtitleLineLength,
        "subtitleLineCount" => AutomatedEvaluationRuleId.SubtitleLineCount,
        "subtitleTiming" => AutomatedEvaluationRuleId.SubtitleTiming,
        "subtitleSafeZone" => AutomatedEvaluationRuleId.SubtitleSafeZone,
        "loudness" => AutomatedEvaluationRuleId.Loudness,
        "truePeak" => AutomatedEvaluationRuleId.TruePeak,
        "provenance" => AutomatedEvaluationRuleId.Provenance,
        "settingsBinding" => AutomatedEvaluationRuleId.SettingsBinding,
        "keyboard" => AutomatedEvaluationRuleId.Keyboard,
        "focus" => AutomatedEvaluationRuleId.Focus,
        "contrast" => AutomatedEvaluationRuleId.Contrast,
        "accessibilityTree" => AutomatedEvaluationRuleId.AccessibilityTree,
        "subtitleSemanticCompleteness" =>
            AutomatedEvaluationRuleId.SubtitleSemanticCompleteness,
        "nonSpeechSoundCoverage" => AutomatedEvaluationRuleId.NonSpeechSoundCoverage,
        "speakerInformationCoverage" =>
            AutomatedEvaluationRuleId.SpeakerInformationCoverage,
        "mainContentUnderstanding" => AutomatedEvaluationRuleId.MainContentUnderstanding,
        "humanReadability" => AutomatedEvaluationRuleId.HumanReadability,
        "naturalness" => AutomatedEvaluationRuleId.Naturalness,
        "perceivedAudioQuality" => AutomatedEvaluationRuleId.PerceivedAudioQuality,
        "emotionalFit" => AutomatedEvaluationRuleId.EmotionalFit,
        "legalCompliance" => AutomatedEvaluationRuleId.LegalCompliance,
        _ => throw new JsonException("The evaluation rule ID is not supported."),
    };

    internal static string ToWireName(AutomatedEvaluationRuleCategory value) => value switch
    {
        AutomatedEvaluationRuleCategory.Machine => "machine",
        AutomatedEvaluationRuleCategory.HumanLimitation => "humanLimitation",
        _ => throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "Unknown evaluation rule category."),
    };

    internal static AutomatedEvaluationRuleCategory ParseRuleCategory(string value) =>
        value switch
        {
            "machine" => AutomatedEvaluationRuleCategory.Machine,
            "humanLimitation" => AutomatedEvaluationRuleCategory.HumanLimitation,
            _ => throw new JsonException("The evaluation rule category is not supported."),
        };

    internal static string ToWireName(AutomatedEvaluationRuleScope value) => value switch
    {
        AutomatedEvaluationRuleScope.Artifact => "artifact",
        AutomatedEvaluationRuleScope.UserInterface => "userInterface",
        AutomatedEvaluationRuleScope.HumanReview => "humanReview",
        _ => throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "Unknown evaluation rule scope."),
    };

    internal static AutomatedEvaluationRuleScope ParseRuleScope(string value) => value switch
    {
        "artifact" => AutomatedEvaluationRuleScope.Artifact,
        "userInterface" => AutomatedEvaluationRuleScope.UserInterface,
        "humanReview" => AutomatedEvaluationRuleScope.HumanReview,
        _ => throw new JsonException("The evaluation rule scope is not supported."),
    };

    internal static string ToWireName(AutomatedEvaluationStatus value) => value switch
    {
        AutomatedEvaluationStatus.Pass => "PASS",
        AutomatedEvaluationStatus.Warning => "WARNING",
        AutomatedEvaluationStatus.Fail => "FAIL",
        AutomatedEvaluationStatus.NotEvaluated => "NOT EVALUATED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown evaluation status."),
    };

    internal static AutomatedEvaluationStatus ParseStatus(string value) => value switch
    {
        "PASS" => AutomatedEvaluationStatus.Pass,
        "WARNING" => AutomatedEvaluationStatus.Warning,
        "FAIL" => AutomatedEvaluationStatus.Fail,
        "NOT EVALUATED" => AutomatedEvaluationStatus.NotEvaluated,
        _ => throw new JsonException("The evaluation status is not supported."),
    };

    internal static string ToWireName(AutomatedEvaluationTargetKind value) => value switch
    {
        AutomatedEvaluationTargetKind.Preview => "preview",
        AutomatedEvaluationTargetKind.Settings => "settings",
        AutomatedEvaluationTargetKind.Evidence => "evidence",
        AutomatedEvaluationTargetKind.Editor => "editor",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown target kind."),
    };

    internal static AutomatedEvaluationTargetKind ParseTargetKind(string value) => value switch
    {
        "preview" => AutomatedEvaluationTargetKind.Preview,
        "settings" => AutomatedEvaluationTargetKind.Settings,
        "evidence" => AutomatedEvaluationTargetKind.Evidence,
        "editor" => AutomatedEvaluationTargetKind.Editor,
        _ => throw new JsonException("The target kind is not supported."),
    };

    internal static string ToWireName(AutomatedEvaluationTimeBasis value) => value switch
    {
        AutomatedEvaluationTimeBasis.Source => "source",
        AutomatedEvaluationTimeBasis.Output => "output",
        _ => throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "Unknown evaluation time basis."),
    };

    internal static AutomatedEvaluationTimeBasis ParseTimeBasis(string value) => value switch
    {
        "source" => AutomatedEvaluationTimeBasis.Source,
        "output" => AutomatedEvaluationTimeBasis.Output,
        _ => throw new JsonException("The evaluation time basis is not supported."),
    };

    internal static string ToWireName(AutomatedEvaluationEditorKind value) => value switch
    {
        AutomatedEvaluationEditorKind.Timeline => "timeline",
        AutomatedEvaluationEditorKind.Subtitles => "subtitles",
        AutomatedEvaluationEditorKind.Audio => "audio",
        AutomatedEvaluationEditorKind.Layout => "layout",
        AutomatedEvaluationEditorKind.Metadata => "metadata",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown editor kind."),
    };

    internal static AutomatedEvaluationEditorKind ParseEditorKind(string value) => value switch
    {
        "timeline" => AutomatedEvaluationEditorKind.Timeline,
        "subtitles" => AutomatedEvaluationEditorKind.Subtitles,
        "audio" => AutomatedEvaluationEditorKind.Audio,
        "layout" => AutomatedEvaluationEditorKind.Layout,
        "metadata" => AutomatedEvaluationEditorKind.Metadata,
        _ => throw new JsonException("The editor kind is not supported."),
    };

    private static bool IsCanonicalSegment(string segment)
    {
        if (segment.Length == 0 || segment[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        char previous = segment[0];
        for (int index = 1; index < segment.Length; index++)
        {
            char current = segment[index];
            if (current is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                previous = current;
                continue;
            }

            if (current != '_' || previous == '_')
            {
                return false;
            }

            previous = current;
        }

        return previous != '_';
    }

    private static void ValidateUnicode(
        string value,
        string parameterName,
        string description)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                $"{description} must contain valid Unicode.",
                parameterName,
                exception);
        }
    }
}
