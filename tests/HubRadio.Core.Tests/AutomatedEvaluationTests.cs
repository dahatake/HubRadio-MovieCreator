using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class AutomatedEvaluationTests
{
    private static readonly EntityId<AutomatedEvaluationIdTag> EvaluationId =
        EntityId<AutomatedEvaluationIdTag>.Parse(
            "10000000-0000-4000-8000-000000000001");

    private static readonly EntityId<JobIdTag> JobId =
        EntityId<JobIdTag>.Parse("20000000-0000-4000-8000-000000000002");

    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("30000000-0000-4000-8000-000000000003");

    private static readonly EntityId<ProjectIdTag> OtherProjectId =
        EntityId<ProjectIdTag>.Parse("30000000-0000-4000-8000-000000000004");

    private static readonly EntityId<CompositionVersionIdTag> VersionId =
        EntityId<CompositionVersionIdTag>.Parse(
            "40000000-0000-4000-8000-000000000004");

    private static readonly EntityId<CompositionVersionIdTag> OtherVersionId =
        EntityId<CompositionVersionIdTag>.Parse(
            "40000000-0000-4000-8000-000000000005");

    private static readonly EntityId<AutomatedEvaluationArtifactIdTag> ArtifactId =
        EntityId<AutomatedEvaluationArtifactIdTag>.Parse(
            "50000000-0000-4000-8000-000000000005");

    private static readonly EntityId<AutomatedEvaluationArtifactIdTag> OtherArtifactId =
        EntityId<AutomatedEvaluationArtifactIdTag>.Parse(
            "50000000-0000-4000-8000-000000000006");

    private static readonly EntityId<DefaultSettingsProfileIdTag> SettingsProfileId =
        EntityId<DefaultSettingsProfileIdTag>.Parse(
            "60000000-0000-4000-8000-000000000006");

    private static readonly SettingsProfileVersion SettingsProfileVersion = new(1, 2, 3);
    private static readonly ContentDigest VersionDigest = Digest('a');
    private static readonly ContentDigest OtherVersionDigest = Digest('b');
    private static readonly ContentDigest ArtifactDigest = Digest('c');
    private static readonly ContentDigest OtherArtifactDigest = Digest('d');
    private static readonly ContentDigest SettingsProfileDigest = Digest('e');
    private static readonly ContentDigest SettingsSnapshotDigest = Digest('f');
    private static readonly ContentDigest EnvironmentDigest = Digest('0');
    private static readonly ContentDigest ToolVersionsDigest = Digest('1');
    private static readonly ContentDigest ModelVersionsDigest = Digest('2');
    private static readonly ContentDigest TemplateDigest = Digest('3');
    private static readonly ContentDigest MusicDigest = Digest('4');
    private static readonly ContentDigest InputDigest = Digest('5');
    private static readonly ContentDigest EvidenceDigest = Digest('6');

    private static readonly DateTimeOffset StartedAt =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    // FR-034 AC-006: 判定対象（規則）の閉じた一覧と分類が定義されている。
    public void ClosedCatalogHasExactCanonicalIdsCategoriesScopesAndImmutableViews()
    {
        string[] expected =
        [
            "codec",
            "fullDecode",
            "duration",
            "avSync",
            "subtitlePresence",
            "subtitleReadingSpeed",
            "subtitleLineLength",
            "subtitleLineCount",
            "subtitleTiming",
            "subtitleSafeZone",
            "loudness",
            "truePeak",
            "provenance",
            "settingsBinding",
            "keyboard",
            "focus",
            "contrast",
            "accessibilityTree",
            "subtitleSemanticCompleteness",
            "nonSpeechSoundCoverage",
            "speakerInformationCoverage",
            "mainContentUnderstanding",
            "humanReadability",
            "naturalness",
            "perceivedAudioQuality",
            "emotionalFit",
            "legalCompliance",
        ];

        CollectionAssert.AreEqual(
            expected,
            AutomatedEvaluationRules.All
                .Select(AutomatedEvaluationRules.ToWireName)
                .ToArray());
        Assert.AreEqual(27, AutomatedEvaluationRules.All.Count);
        Assert.AreEqual(18, AutomatedEvaluationRules.Machine.Count);
        Assert.AreEqual(9, AutomatedEvaluationRules.HumanLimitations.Count);
        Assert.AreEqual(14, AutomatedEvaluationRules.Definitions.Count(static definition =>
            definition.Scope == AutomatedEvaluationRuleScope.Artifact));
        Assert.AreEqual(4, AutomatedEvaluationRules.Definitions.Count(static definition =>
            definition.Scope == AutomatedEvaluationRuleScope.UserInterface));
        Assert.AreEqual(9, AutomatedEvaluationRules.Definitions.Count(static definition =>
            definition.Scope == AutomatedEvaluationRuleScope.HumanReview));
        Assert.IsTrue(AutomatedEvaluationRules.Definitions
            .Take(18)
            .All(static definition =>
                definition.Category == AutomatedEvaluationRuleCategory.Machine));
        Assert.IsTrue(AutomatedEvaluationRules.Definitions
            .Skip(18)
            .All(static definition =>
                definition.Category == AutomatedEvaluationRuleCategory.HumanLimitation));
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AutomatedEvaluationRuleId>)AutomatedEvaluationRules.All).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AutomatedEvaluationRuleDefinition>)
                AutomatedEvaluationRules.Definitions).Clear());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AutomatedEvaluationRules.GetDefinition((AutomatedEvaluationRuleId)int.MaxValue));
    }

    [TestMethod]
    public void ArtifactlessRunCanPassOnlyUiRulesAndCannotReportAggregatePass()
    {
        AutomatedEvaluationRuleObservation[] uiPasses =
            AutomatedEvaluationRules.Definitions
                .Where(static definition =>
                    definition.Scope == AutomatedEvaluationRuleScope.UserInterface)
                .Select(static definition => Pass(definition.RuleId))
                .ToArray();

        AutomatedEvaluationRun run = new AutomatedEvaluationEngine().Evaluate(
            CreateRequest(uiPasses, includeArtifact: false));

        Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, run.AggregateStatus);
        Assert.AreEqual(18, run.Rules.Count);
        Assert.AreEqual(9, run.Limitations.Count);
        Assert.AreEqual(4, run.StatusCounts.Pass);
        Assert.AreEqual(0, run.StatusCounts.Warning);
        Assert.AreEqual(0, run.StatusCounts.Fail);
        Assert.AreEqual(23, run.StatusCounts.NotEvaluated);
        Assert.AreEqual(27, run.StatusCounts.Total);
        Assert.IsTrue(run.Rules
            .Where(static result => result.Scope == AutomatedEvaluationRuleScope.Artifact)
            .All(static result =>
                result.Status == AutomatedEvaluationStatus.NotEvaluated &&
                result.ReasonCode == AutomatedEvaluationEngine.ArtifactUnavailableReasonCode &&
                result.EvidenceDigest is null));
        Assert.IsTrue(run.Rules
            .Where(static result =>
                result.Scope == AutomatedEvaluationRuleScope.UserInterface)
            .All(static result => result.Status == AutomatedEvaluationStatus.Pass));
        Assert.IsTrue(run.Limitations.All(static result =>
            result.Status == AutomatedEvaluationStatus.NotEvaluated &&
            result.ReasonCode == AutomatedEvaluationEngine.HumanReviewRequiredReasonCode));

        Assert.ThrowsExactly<ArgumentException>(() =>
            new AutomatedEvaluationEngine().Evaluate(
                CreateRequest([Pass(AutomatedEvaluationRuleId.Codec)], includeArtifact: false)));
    }

    [TestMethod]
    // FR-034 AC-006: 判定は PASS になり得るが、人の評価ではない制限が表示され続ける。
    public void CompleteArtifactBoundMachineSurfaceCanPassWhileLimitationsStayVisible()
    {
        AutomatedEvaluationRun run = new AutomatedEvaluationEngine().Evaluate(
            CreateRequest(AllMachinePass()));

        Assert.AreEqual(AutomatedEvaluationStatus.Pass, run.AggregateStatus);
        Assert.IsTrue(run.Rules.All(static result =>
            result.Status == AutomatedEvaluationStatus.Pass));
        Assert.IsTrue(run.Limitations.All(static result =>
            result.Status == AutomatedEvaluationStatus.NotEvaluated));
        Assert.AreEqual(18, run.StatusCounts.Pass);
        Assert.AreEqual(9, run.StatusCounts.NotEvaluated);
        Assert.AreEqual(27, run.StatusCounts.Total);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AutomatedEvaluationRuleResult>)run.Rules).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AutomatedEvaluationRuleResult>)run.Limitations).Clear());
        Assert.IsTrue(typeof(AutomatedEvaluationStatusCounts)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .All(static property => property.SetMethod is null));
    }

    [TestMethod]
    // FR-034 AC-006: 規則ごとの PASS・WARNING・FAIL・未評価が集約される。
    public void AggregatePrecedenceIsFailThenNotEvaluatedThenWarningThenPass()
    {
        var engine = new AutomatedEvaluationEngine();
        AutomatedEvaluationRun pass = engine.Evaluate(CreateRequest(AllMachinePass()));
        AutomatedEvaluationRun warning = engine.Evaluate(CreateRequest(Replace(
            AllMachinePass(),
            Warning(AutomatedEvaluationRuleId.Loudness))));
        AutomatedEvaluationRun notEvaluated = engine.Evaluate(CreateRequest(Replace(
            Replace(AllMachinePass(), Warning(AutomatedEvaluationRuleId.Loudness)),
            NotEvaluated(AutomatedEvaluationRuleId.Keyboard))));
        AutomatedEvaluationRun fail = engine.Evaluate(CreateRequest(Replace(
            Replace(
                Replace(AllMachinePass(), Warning(AutomatedEvaluationRuleId.Loudness)),
                NotEvaluated(AutomatedEvaluationRuleId.Keyboard)),
            Fail(AutomatedEvaluationRuleId.FullDecode))));

        Assert.AreEqual(AutomatedEvaluationStatus.Pass, pass.AggregateStatus);
        Assert.AreEqual(AutomatedEvaluationStatus.Warning, warning.AggregateStatus);
        Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, notEvaluated.AggregateStatus);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, fail.AggregateStatus);
        Assert.AreEqual(1, fail.StatusCounts.Warning);
        Assert.AreEqual(1, fail.StatusCounts.Fail);
        Assert.AreEqual(10, fail.StatusCounts.NotEvaluated);
    }

    [TestMethod]
    public void MissingMachineObservationIsExplicitNotEvaluated()
    {
        AutomatedEvaluationRun run = new AutomatedEvaluationEngine().Evaluate(
            CreateRequest(AllMachinePass().Where(static observation =>
                observation.RuleId != AutomatedEvaluationRuleId.Keyboard).ToArray()));

        AutomatedEvaluationRuleResult missing = run.Rules.Single(static result =>
            result.RuleId == AutomatedEvaluationRuleId.Keyboard);
        Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, missing.Status);
        Assert.AreEqual(
            AutomatedEvaluationEngine.MissingObservationReasonCode,
            missing.ReasonCode);
        Assert.IsNull(missing.EvidenceDigest);
        Assert.IsNull(missing.Target);
        Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, run.AggregateStatus);
    }

    [TestMethod]
    public void PublicFactoriesAreTheOnlyObservationTrustBoundary()
    {
        Assert.AreEqual(0, typeof(AutomatedEvaluationRuleObservation).GetConstructors().Length);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            AutomatedEvaluationRuleObservation.ObjectivePass(
                AutomatedEvaluationRuleId.Codec,
                "evaluation.rule_passed",
                null!));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AutomatedEvaluationRuleObservation.NotEvaluated(
                AutomatedEvaluationRuleId.SubtitleSemanticCompleteness,
                "evaluation.rule_not_evaluated"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AutomatedEvaluationRuleObservation.ObjectivePass(
                AutomatedEvaluationRuleId.Codec,
                "Invalid",
                EvidenceDigest));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AutomatedEvaluationRuleObservation.ObjectivePass(
                (AutomatedEvaluationRuleId)int.MaxValue,
                "evaluation.rule_passed",
                EvidenceDigest));
    }

    [TestMethod]
    public void RequestRequiresAuditBindingsExactOutputDigestAndUtcOrdering()
    {
        AutomatedEvaluationRequest request = CreateRequest(AllMachinePass());
        Assert.AreEqual(SettingsProfileId, request.SettingsProfileId);
        Assert.AreEqual(SettingsProfileVersion, request.SettingsProfileVersion);
        Assert.AreEqual(SettingsProfileDigest, request.SettingsProfileDigest);
        Assert.AreEqual(EnvironmentDigest, request.EnvironmentDigest);
        Assert.AreEqual(ToolVersionsDigest, request.ToolVersionsDigest);
        Assert.AreEqual(ModelVersionsDigest, request.ModelVersionsDigest);
        Assert.AreEqual(TemplateDigest, request.TemplateDigest);
        Assert.AreEqual(MusicDigest, request.MusicDigest);

        Assert.ThrowsExactly<ArgumentException>(() => CreateRequest(
            AllMachinePass(),
            artifactDigest: ArtifactDigest,
            outputDigest: OtherArtifactDigest));
        Assert.ThrowsExactly<ArgumentException>(() => CreateRequest(
            AllMachinePass(),
            includeArtifact: false,
            outputDigest: ArtifactDigest));
        Assert.ThrowsExactly<ArgumentException>(() => CreateRequest(
            AllMachinePass(),
            environmentVersion: " environment-v1"));
        Assert.ThrowsExactly<ArgumentException>(() => CreateRequest(
            AllMachinePass(),
            startedAt: StartedAt.ToOffset(TimeSpan.FromHours(9))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRequest(
            AllMachinePass(),
            completedAt: StartedAt.AddTicks(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRequest(
            AllMachinePass(),
            completedAt: StartedAt.AddSeconds(1),
            evaluatedAt: StartedAt));
        Assert.ThrowsExactly<ArgumentException>(() => CreateRequest(
            [Pass(AutomatedEvaluationRuleId.Codec), Pass(AutomatedEvaluationRuleId.Codec)]));
    }

    [TestMethod]
    public void TargetsHaveFiniteShapesAndEngineValidatesScopeAndExactBindings()
    {
        RationalTime time = new(25, 24);
        AutomatedEvaluationTarget preview = AutomatedEvaluationTarget.Preview(
            ProjectId,
            VersionId,
            VersionDigest,
            AutomatedEvaluationTimeBasis.Output,
            time,
            ArtifactId,
            ArtifactDigest);
        AutomatedEvaluationTarget settings = AutomatedEvaluationTarget.Settings(
            ProjectId,
            VersionId,
            VersionDigest,
            "subtitles.maximumLineLength",
            ArtifactId,
            ArtifactDigest);
        AutomatedEvaluationTarget evidence = AutomatedEvaluationTarget.Evidence(
            ProjectId,
            VersionId,
            VersionDigest,
            ArtifactId,
            ArtifactDigest);
        AutomatedEvaluationTarget editor = AutomatedEvaluationTarget.Editor(
            ProjectId,
            VersionId,
            VersionDigest,
            AutomatedEvaluationEditorKind.Subtitles,
            ArtifactId,
            ArtifactDigest);

        Assert.AreEqual(AutomatedEvaluationTimeBasis.Output, preview.TimeBasis);
        Assert.AreEqual(time, preview.Time);
        Assert.IsNull(preview.SettingKey);
        Assert.IsNull(preview.EditorKind);
        Assert.AreEqual("subtitles.maximumLineLength", settings.SettingKey);
        Assert.IsNull(settings.TimeBasis);
        Assert.AreEqual(AutomatedEvaluationTargetKind.Evidence, evidence.Kind);
        Assert.AreEqual(AutomatedEvaluationEditorKind.Subtitles, editor.EditorKind);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AutomatedEvaluationTarget.Preview(
                ProjectId,
                VersionId,
                VersionDigest,
                AutomatedEvaluationTimeBasis.Source,
                new RationalTime(-1, 1)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AutomatedEvaluationTarget.Settings(
                ProjectId,
                VersionId,
                VersionDigest,
                "Subtitles.invalid"));

        AutomatedEvaluationTarget[] invalidBindings =
        [
            AutomatedEvaluationTarget.Evidence(
                OtherProjectId,
                VersionId,
                VersionDigest,
                ArtifactId,
                ArtifactDigest),
            AutomatedEvaluationTarget.Evidence(
                ProjectId,
                OtherVersionId,
                VersionDigest,
                ArtifactId,
                ArtifactDigest),
            AutomatedEvaluationTarget.Evidence(
                ProjectId,
                VersionId,
                OtherVersionDigest,
                ArtifactId,
                ArtifactDigest),
            AutomatedEvaluationTarget.Evidence(
                ProjectId,
                VersionId,
                VersionDigest,
                OtherArtifactId,
                OtherArtifactDigest),
        ];
        foreach (AutomatedEvaluationTarget target in invalidBindings)
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                new AutomatedEvaluationEngine().Evaluate(CreateRequest(Replace(
                    AllMachinePass(),
                    AutomatedEvaluationRuleObservation.ObjectiveFail(
                        AutomatedEvaluationRuleId.Loudness,
                        "evaluation.rule_failed",
                        EvidenceDigest,
                        target)))));
        }
    }

    [TestMethod]
    public void FailureTargetAndAuditEvidenceRoundTripCanonically()
    {
        AutomatedEvaluationTarget target = AutomatedEvaluationTarget.Preview(
            ProjectId,
            VersionId,
            VersionDigest,
            AutomatedEvaluationTimeBasis.Source,
            new RationalTime(50, 24),
            ArtifactId,
            ArtifactDigest);
        AutomatedEvaluationRuleObservation failure =
            AutomatedEvaluationRuleObservation.ObjectiveFail(
                AutomatedEvaluationRuleId.SubtitleTiming,
                "evaluation.rule_failed",
                EvidenceDigest,
                target);
        AutomatedEvaluationRun original = new AutomatedEvaluationEngine().Evaluate(
            CreateRequest(Replace(AllMachinePass(), failure)));

        Assert.AreEqual(
            original.CanonicalJson,
            CanonicalJson.Canonicalize(original.CanonicalJson));
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(original.CanonicalJson)),
            original.CanonicalDigest);
        StringAssert.Contains(original.CanonicalJson, "\"category\":\"humanLimitation\"");
        StringAssert.Contains(original.CanonicalJson, "\"scope\":\"artifact\"");
        StringAssert.Contains(original.CanonicalJson, "\"timeBasis\":\"source\"");
        StringAssert.Contains(original.CanonicalJson, "\"statusCounts\"");

        AutomatedEvaluationRun parsed = AutomatedEvaluationRun.ParseCanonical(
            original.CanonicalJson);
        Assert.AreEqual(original.CanonicalJson, parsed.CanonicalJson);
        Assert.AreEqual(original.CanonicalDigest, parsed.CanonicalDigest);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, parsed.AggregateStatus);
        Assert.AreEqual(18, parsed.Rules.Count);
        Assert.AreEqual(9, parsed.Limitations.Count);
        AutomatedEvaluationRuleResult parsedFailure = parsed.Rules.Single(static result =>
            result.RuleId == AutomatedEvaluationRuleId.SubtitleTiming);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, parsedFailure.Status);
        Assert.AreEqual(AutomatedEvaluationTimeBasis.Source, parsedFailure.Target!.TimeBasis);
        Assert.AreEqual(new RationalTime(50, 24), parsedFailure.Target.Time);
    }

    [TestMethod]
    public void ArtifactlessDerivedNotEvaluatedResultsRoundTripCanonically()
    {
        AutomatedEvaluationRuleObservation[] uiPasses =
            AutomatedEvaluationRules.Definitions
                .Where(static definition =>
                    definition.Scope == AutomatedEvaluationRuleScope.UserInterface)
                .Select(static definition => Pass(definition.RuleId))
                .ToArray();
        AutomatedEvaluationRun original = new AutomatedEvaluationEngine().Evaluate(
            CreateRequest(uiPasses, includeArtifact: false));

        AutomatedEvaluationRun parsed = AutomatedEvaluationRun.ParseCanonical(
            original.CanonicalJson);

        Assert.AreEqual(original.CanonicalJson, parsed.CanonicalJson);
        Assert.AreEqual(original.CanonicalDigest, parsed.CanonicalDigest);
        Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, parsed.AggregateStatus);
        Assert.AreEqual(4, parsed.StatusCounts.Pass);
        Assert.AreEqual(23, parsed.StatusCounts.NotEvaluated);
        Assert.IsTrue(parsed.Rules
            .Where(static result =>
                result.Scope == AutomatedEvaluationRuleScope.Artifact)
            .All(static result =>
                result.Status == AutomatedEvaluationStatus.NotEvaluated &&
                result.ReasonCode ==
                    AutomatedEvaluationEngine.ArtifactUnavailableReasonCode));
    }

    [TestMethod]
    public void CanonicalParserRejectsUnknownMissingReorderedOrDerivedTampering()
    {
        AutomatedEvaluationRun run = new AutomatedEvaluationEngine().Evaluate(
            CreateRequest(AllMachinePass()));

        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            string.Concat(" ", run.CanonicalJson)));
        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            Mutate(run.CanonicalJson, static root => root["unknown"] = true)));
        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            Mutate(run.CanonicalJson, static root => root.Remove("toolVersionsDigest"))));
        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            Mutate(run.CanonicalJson, static root => root["aggregateStatus"] = "FAIL")));
        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            Mutate(
                run.CanonicalJson,
                static root => root["statusCounts"]!["pass"] = 17)));
        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            Mutate(
                run.CanonicalJson,
                static root => root["rules"]!.AsArray()[0]!["scope"] = "userInterface")));
        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            Mutate(
                run.CanonicalJson,
                static root => root["limitations"]!.AsArray()[0]!["status"] = "PASS")));
        Assert.Throws<JsonException>(() => AutomatedEvaluationRun.ParseCanonical(
            Mutate(run.CanonicalJson, static root => Reverse(root["rules"]!.AsArray()))));
    }

    private static AutomatedEvaluationRequest CreateRequest(
        IReadOnlyList<AutomatedEvaluationRuleObservation> observations,
        bool includeArtifact = true,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null,
        ContentDigest? artifactDigest = null,
        ContentDigest? outputDigest = null,
        string environmentVersion = "ffmpeg-8.0.1_windows-x64",
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? evaluatedAt = null)
    {
        EntityId<AutomatedEvaluationArtifactIdTag>? selectedArtifactId =
            includeArtifact ? artifactId ?? ArtifactId : artifactId;
        ContentDigest? selectedArtifactDigest =
            includeArtifact ? artifactDigest ?? ArtifactDigest : artifactDigest;
        ContentDigest? selectedOutputDigest =
            includeArtifact ? outputDigest ?? selectedArtifactDigest : outputDigest;

        return new AutomatedEvaluationRequest(
            EvaluationId,
            JobId,
            ProjectId,
            VersionId,
            VersionDigest,
            selectedArtifactId,
            selectedArtifactDigest,
            SettingsProfileId,
            SettingsProfileVersion,
            SettingsProfileDigest,
            SettingsSnapshotDigest,
            new SettingsProfileVersion(2, 0, 0),
            environmentVersion,
            EnvironmentDigest,
            ToolVersionsDigest,
            ModelVersionsDigest,
            TemplateDigest,
            MusicDigest,
            startedAt ?? StartedAt,
            completedAt ?? StartedAt.AddSeconds(10),
            evaluatedAt ?? StartedAt.AddSeconds(11),
            InputDigest,
            selectedOutputDigest,
            observations);
    }

    private static AutomatedEvaluationRuleObservation[] AllMachinePass() =>
        AutomatedEvaluationRules.Machine.Select(Pass).ToArray();

    private static AutomatedEvaluationRuleObservation Pass(
        AutomatedEvaluationRuleId ruleId) =>
        AutomatedEvaluationRuleObservation.ObjectivePass(
            ruleId,
            "evaluation.rule_passed",
            EvidenceDigest);

    private static AutomatedEvaluationRuleObservation Warning(
        AutomatedEvaluationRuleId ruleId) =>
        AutomatedEvaluationRuleObservation.ObjectiveWarning(
            ruleId,
            "evaluation.objective_warning",
            EvidenceDigest);

    private static AutomatedEvaluationRuleObservation Fail(
        AutomatedEvaluationRuleId ruleId) =>
        AutomatedEvaluationRuleObservation.ObjectiveFail(
            ruleId,
            "evaluation.rule_failed",
            EvidenceDigest);

    private static AutomatedEvaluationRuleObservation NotEvaluated(
        AutomatedEvaluationRuleId ruleId) =>
        AutomatedEvaluationRuleObservation.NotEvaluated(
            ruleId,
            "evaluation.rule_not_evaluated");

    private static AutomatedEvaluationRuleObservation[] Replace(
        IReadOnlyList<AutomatedEvaluationRuleObservation> source,
        AutomatedEvaluationRuleObservation replacement) =>
        source.Select(observation => observation.RuleId == replacement.RuleId
            ? replacement
            : observation).ToArray();

    private static ContentDigest Digest(char character) =>
        new(new string(character, 64));

    private static string Mutate(
        string canonicalJson,
        Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(canonicalJson)!.AsObject();
        mutation(root);
        return CanonicalJson.Canonicalize(root.ToJsonString());
    }

    private static void Reverse(JsonArray array)
    {
        JsonNode?[] reversed = array
            .Select(static node => node?.DeepClone())
            .Reverse()
            .ToArray();
        array.Clear();
        foreach (JsonNode? node in reversed)
        {
            array.Add(node);
        }
    }
}
