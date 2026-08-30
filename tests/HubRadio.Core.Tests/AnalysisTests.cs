using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class AnalysisTests
{
    [TestMethod]
    public void AnalysisTypesAreSealedImmutableAndUseTypedIds()
    {
        CollectionAssert.AreEqual(
            new[] { ModelExecutionLocation.Local, ModelExecutionLocation.Cloud },
            Enum.GetValues<ModelExecutionLocation>());
        CollectionAssert.AreEqual(
            new[] { ModelHashAvailability.Available, ModelHashAvailability.NotDisclosed },
            Enum.GetValues<ModelHashAvailability>());
        CollectionAssert.AreEqual(
            new[]
            {
                ModelRunOutcome.Succeeded,
                ModelRunOutcome.Failed,
                ModelRunOutcome.TimedOut,
                ModelRunOutcome.Canceled,
            },
            Enum.GetValues<ModelRunOutcome>());
        CollectionAssert.AreEqual(
            new[]
            {
                DetectionKind.Silence,
                DetectionKind.Repetition,
                DetectionKind.PreparationConversation,
                DetectionKind.RecordingProblem,
                DetectionKind.ContextDeviation,
                DetectionKind.AudioEvent,
                DetectionKind.EditorialInstruction,
                DetectionKind.TopicBoundary,
            },
            Enum.GetValues<DetectionKind>());
        CollectionAssert.AreEqual(
            new[]
            {
                SuggestionKind.DeleteRange,
                SuggestionKind.TopicBoundary,
                SuggestionKind.Highlight,
                SuggestionKind.TermCorrection,
            },
            Enum.GetValues<SuggestionKind>());
        CollectionAssert.AreEqual(
            new[]
            {
                SuggestionStatus.Proposed,
                SuggestionStatus.Reviewed,
                SuggestionStatus.Rejected,
                SuggestionStatus.Adopted,
            },
            Enum.GetValues<SuggestionStatus>());

        foreach (Type marker in new[]
        {
            typeof(DetectionIdTag),
            typeof(SuggestionIdTag),
            typeof(ModelRunIdTag),
        })
        {
            Assert.IsTrue(marker.IsPublic);
            Assert.IsTrue(marker.IsSealed);
        }

        AssertPublicGetOnlyProperties(
            typeof(ModelRun),
            "Id",
            "Provider",
            "ModelIdentifier",
            "ModelVersion",
            "ModelHash",
            "ModelHashAvailability",
            "ConfigurationDigest",
            "InputDigest",
            "OutputDigest",
            "Outcome",
            "ErrorCode",
            "ExecutionLocation",
            "Destination",
            "Region",
            "SentDataClassification",
            "RetentionPolicy",
            "StartedAt",
            "CompletedAt");
        AssertPublicGetOnlyProperties(
            typeof(Detection),
            "Id",
            "ModelRunId",
            "Kind",
            "Origin",
            "Description",
            "Confidence");
        AssertPublicGetOnlyProperties(
            typeof(Suggestion),
            "Id",
            "ModelRunId",
            "Kind",
            "Status",
            "Origin",
            "Reason",
            "Confidence",
            "MissingContext",
            "Warning");
        AssertPublicGetOnlyProperties(typeof(SuggestionReference), "SuggestionId", "Origin");

        Assert.AreEqual(
            typeof(EntityId<ModelRunIdTag>),
            typeof(ModelRun).GetProperty("Id")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<DetectionIdTag>),
            typeof(Detection).GetProperty("Id")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<SuggestionIdTag>),
            typeof(SuggestionReference).GetProperty("SuggestionId")!.PropertyType);
    }

    [TestMethod]
    public void LocalModelRunKeepsDigestsAndHasNoTransmissionMetadata()
    {
        DateTimeOffset started = new(2026, 8, 29, 1, 2, 3, TimeSpan.Zero);
        DateTimeOffset completed = started.AddSeconds(12);
        ModelRun run = CreateModelRun(startedAt: started, completedAt: completed);

        Assert.AreEqual("local-rules", run.Provider);
        Assert.AreEqual("silence-detector", run.ModelIdentifier);
        Assert.AreEqual("1.0.0", run.ModelVersion);
        Assert.AreEqual(Digest('a'), run.ModelHash);
        Assert.AreEqual(ModelHashAvailability.Available, run.ModelHashAvailability);
        Assert.AreEqual(Digest('d'), run.ConfigurationDigest);
        Assert.AreEqual(Digest('b'), run.InputDigest);
        Assert.AreEqual(Digest('c'), run.OutputDigest);
        Assert.AreEqual(ModelRunOutcome.Succeeded, run.Outcome);
        Assert.IsNull(run.ErrorCode);
        Assert.AreEqual(ModelExecutionLocation.Local, run.ExecutionLocation);
        Assert.IsNull(run.Destination);
        Assert.IsNull(run.Region);
        Assert.IsNull(run.SentDataClassification);
        Assert.IsNull(run.RetentionPolicy);
        Assert.AreEqual(started, run.StartedAt);
        Assert.AreEqual(completed, run.CompletedAt);
    }

    [TestMethod]
    public void CloudModelRunRequiresCompleteTrimmedTransmissionMetadata()
    {
        ModelRun run = CreateModelRun(
            executionLocation: ModelExecutionLocation.Cloud,
            destination: "approved-service",
            region: "approved-region",
            sentDataClassification: "selected-masked-transcript-span",
            retentionPolicy: "approved-policy");

        Assert.AreEqual(ModelExecutionLocation.Cloud, run.ExecutionLocation);
        Assert.AreEqual("approved-service", run.Destination);
        Assert.AreEqual("approved-region", run.Region);
        Assert.AreEqual("selected-masked-transcript-span", run.SentDataClassification);
        Assert.AreEqual("approved-policy", run.RetentionPolicy);

        string?[] invalid = [null, string.Empty, " ", " value", "value "];
        foreach (string? value in invalid)
        {
            Action create = () => CreateModelRun(
                executionLocation: ModelExecutionLocation.Cloud,
                destination: value,
                region: "region",
                sentDataClassification: "classification",
                retentionPolicy: "policy");
            ArgumentException exception = value is null
                ? Assert.ThrowsExactly<ArgumentNullException>(create)
                : Assert.ThrowsExactly<ArgumentException>(create);
            Assert.AreEqual("destination", exception.ParamName);
        }
    }

    [TestMethod]
    public void LocalModelRunRejectsAnyClaimedCloudMetadata()
    {
        (string Parameter, Func<ModelRun> Create)[] invalid =
        [
            ("destination", () => CreateModelRun(destination: "service")),
            ("region", () => CreateModelRun(region: "region")),
            ("sentDataClassification", () => CreateModelRun(sentDataClassification: "classification")),
            ("retentionPolicy", () => CreateModelRun(retentionPolicy: "policy")),
        ];

        foreach ((string parameter, Func<ModelRun> create) in invalid)
        {
            Assert.AreEqual(
                parameter,
                Assert.ThrowsExactly<ArgumentException>(() => create()).ParamName);
        }
    }

    [TestMethod]
    public void CloudModelRunCanRecordUndisclosedHashWithoutInventingDigest()
    {
        ModelRun run = CreateModelRun(
            modelHashAvailability: ModelHashAvailability.NotDisclosed,
            executionLocation: ModelExecutionLocation.Cloud,
            destination: "approved-service",
            region: "approved-region",
            sentDataClassification: "selected-masked-transcript-span",
            retentionPolicy: "approved-policy");

        Assert.IsNull(run.ModelHash);
        Assert.AreEqual(ModelHashAvailability.NotDisclosed, run.ModelHashAvailability);

        Assert.AreEqual(
            "modelHashAvailability",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateModelRun(
                    modelHashAvailability: ModelHashAvailability.NotDisclosed)).ParamName);
    }

    [TestMethod]
    public void ModelHashAvailabilityMustMatchTheSuppliedHash()
    {
        Assert.AreEqual(
            "modelHash",
            Assert.ThrowsExactly<ArgumentNullException>(
                () => NewModelRun(
                    modelHash: null,
                    modelHashAvailability: ModelHashAvailability.Available)).ParamName);
        Assert.AreEqual(
            "modelHash",
            Assert.ThrowsExactly<ArgumentException>(
                () => NewModelRun(
                    modelHash: Digest('a'),
                    modelHashAvailability: ModelHashAvailability.NotDisclosed,
                    executionLocation: ModelExecutionLocation.Cloud,
                    destination: "service",
                    region: "region",
                    sentDataClassification: "classification",
                    retentionPolicy: "policy")).ParamName);
        Assert.AreEqual(
            "modelHashAvailability",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => NewModelRun(
                    modelHash: Digest('a'),
                    modelHashAvailability: (ModelHashAvailability)99)).ParamName);
    }

    [TestMethod]
    public void UnsuccessfulModelRunsHaveCanonicalErrorCodeAndNoSuccessfulOutput()
    {
        foreach (ModelRunOutcome outcome in new[]
        {
            ModelRunOutcome.Failed,
            ModelRunOutcome.TimedOut,
            ModelRunOutcome.Canceled,
        })
        {
            ModelRun run = CreateModelRun(outcome: outcome, errorCode: "model.run_failed");
            Assert.AreEqual(outcome, run.Outcome);
            Assert.IsNull(run.OutputDigest);
            Assert.AreEqual("model.run_failed", run.ErrorCode);
        }

        Assert.AreEqual(
            "errorCode",
            Assert.ThrowsExactly<ArgumentNullException>(
                () => CreateModelRun(outcome: ModelRunOutcome.Failed)).ParamName);
        Assert.AreEqual(
            "outputDigest",
            Assert.ThrowsExactly<ArgumentException>(
                () => NewModelRun(
                    modelHash: Digest('a'),
                    outputDigest: Digest('c'),
                    outcome: ModelRunOutcome.Failed,
                    errorCode: "model.run_failed")).ParamName);

        foreach (string code in new[]
        {
            "MODEL.RUN_FAILED",
            "model",
            "model..failed",
            "model.-failed",
            "model.run__failed",
            $"model.{new string('a', 59)}",
        })
        {
            Assert.AreEqual(
                "errorCode",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateModelRun(
                        outcome: ModelRunOutcome.Failed,
                        errorCode: code)).ParamName);
        }
    }

    [TestMethod]
    public void SuccessfulModelRunRequiresOutputAndForbidsErrorCode()
    {
        Assert.AreEqual(
            "outputDigest",
            Assert.ThrowsExactly<ArgumentNullException>(
                () => NewModelRun(modelHash: Digest('a'), outputDigest: null)).ParamName);
        Assert.AreEqual(
            "errorCode",
            Assert.ThrowsExactly<ArgumentException>(
                () => NewModelRun(
                    modelHash: Digest('a'),
                    outputDigest: Digest('c'),
                    errorCode: "model.run_failed")).ParamName);
        Assert.AreEqual(
            "outcome",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => NewModelRun(
                    modelHash: Digest('a'),
                    outputDigest: Digest('c'),
                    outcome: (ModelRunOutcome)99)).ParamName);
    }

    [TestMethod]
    public void ModelRunRejectsUnknownLocationInvalidTextAndReverseTime()
    {
        Assert.AreEqual(
            "executionLocation",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateModelRun(executionLocation: (ModelExecutionLocation)99)).ParamName);

        foreach (string value in new[] { string.Empty, " ", " model", "model " })
        {
            Assert.AreEqual(
                "modelIdentifier",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateModelRun(modelIdentifier: value)).ParamName);
        }

        DateTimeOffset started = new(2026, 8, 29, 0, 0, 1, TimeSpan.Zero);
        Assert.AreEqual(
            "completedAt",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateModelRun(startedAt: started, completedAt: started.AddTicks(-1))).ParamName);
    }

    [TestMethod]
    public void DetectionPreservesModelRunSourceEvidenceAndOptionalConfidence()
    {
        EntityId<ModelRunIdTag> runId = EntityId<ModelRunIdTag>.New();
        OriginRef origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        Detection detection = new(
            EntityId<DetectionIdTag>.New(),
            runId,
            DetectionKind.AudioEvent,
            origin,
            "A cough-like audio event was observed.",
            confidence: null);

        Assert.AreEqual(runId, detection.ModelRunId);
        Assert.AreEqual(DetectionKind.AudioEvent, detection.Kind);
        Assert.AreSame(origin, detection.Origin);
        Assert.AreEqual("A cough-like audio event was observed.", detection.Description);
        Assert.IsNull(detection.Confidence);
    }

    [TestMethod]
    public void DetectionAndSuggestionRequireTimeBoundedSourceSpanOrigins()
    {
        OriginRef asset = OriginRef.FromAsset(EntityId<AssetIdTag>.New());
        OriginRef editorial = OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New());

        foreach (OriginRef invalidOrigin in new[] { asset, editorial })
        {
            Assert.AreEqual(
                "origin",
                Assert.ThrowsExactly<ArgumentException>(
                    () => new Detection(
                        EntityId<DetectionIdTag>.New(),
                        EntityId<ModelRunIdTag>.New(),
                        DetectionKind.Silence,
                        invalidOrigin,
                        "Invalid machine origin.",
                        null)).ParamName);
            Assert.AreEqual(
                "origin",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateSuggestion(origin: invalidOrigin)).ParamName);
        }
    }

    [TestMethod]
    public void DetectionAndSuggestionRejectUnknownEnums()
    {
        Assert.AreEqual(
            "kind",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateSuggestion(kind: (SuggestionKind)99)).ParamName);
        Assert.AreEqual(
            "status",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateSuggestion(status: (SuggestionStatus)99)).ParamName);
    }

    [TestMethod]
    public void AnalysisConfidenceIsOptionalAndFiniteClosedUnitInterval()
    {
        foreach (double? value in new double?[] { null, 0d, 1d })
        {
            Detection detection = new(
                EntityId<DetectionIdTag>.New(),
                EntityId<ModelRunIdTag>.New(),
                DetectionKind.Silence,
                OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New()),
                "Silence observed.",
                value);
            Assert.AreEqual(value, detection.Confidence);
            Assert.AreEqual(value, CreateSuggestion(confidence: value).Confidence);
        }

        foreach (double value in new[]
        {
            -double.Epsilon,
            Math.BitIncrement(1d),
            double.NaN,
            double.NegativeInfinity,
            double.PositiveInfinity,
        })
        {
            Assert.AreEqual(
                "confidence",
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => CreateSuggestion(confidence: value)).ParamName);
        }
    }

    [TestMethod]
    public void SuggestionStoresReasonMissingContextWarningAndEveryReviewState()
    {
        foreach (SuggestionStatus status in Enum.GetValues<SuggestionStatus>())
        {
            Suggestion suggestion = CreateSuggestion(
                status: status,
                missingContext: "The preceding definition is omitted.",
                warning: "Internal terminology needs review.");

            Assert.AreEqual(status, suggestion.Status);
            Assert.AreEqual("Candidate is independently understandable.", suggestion.Reason);
            Assert.AreEqual("The preceding definition is omitted.", suggestion.MissingContext);
            Assert.AreEqual("Internal terminology needs review.", suggestion.Warning);
        }
    }

    [TestMethod]
    public void SuggestionTextRejectsBlankOrUntrimmedValuesWithoutInventingDefaults()
    {
        foreach (string value in new[] { string.Empty, " ", " text", "text " })
        {
            Assert.AreEqual(
                "reason",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateSuggestion(reason: value)).ParamName);
            Assert.AreEqual(
                "missingContext",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateSuggestion(missingContext: value)).ParamName);
            Assert.AreEqual(
                "warning",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateSuggestion(warning: value)).ParamName);
        }

        Suggestion suggestion = CreateSuggestion();
        Assert.IsNull(suggestion.Confidence);
        Assert.IsNull(suggestion.MissingContext);
        Assert.IsNull(suggestion.Warning);
    }

    [TestMethod]
    public void OnlyAdoptedSuggestionCreatesReferenceWithSameIdAndOrigin()
    {
        Suggestion adopted = CreateSuggestion(status: SuggestionStatus.Adopted);
        SuggestionReference reference = SuggestionReference.FromAdoptedSuggestion(adopted);

        Assert.AreEqual(adopted.Id, reference.SuggestionId);
        Assert.AreSame(adopted.Origin, reference.Origin);

        foreach (SuggestionStatus status in new[]
        {
            SuggestionStatus.Proposed,
            SuggestionStatus.Reviewed,
            SuggestionStatus.Rejected,
        })
        {
            Assert.AreEqual(
                "suggestion",
                Assert.ThrowsExactly<ArgumentException>(
                    () => SuggestionReference.FromAdoptedSuggestion(
                        CreateSuggestion(status: status))).ParamName);
        }

        Assert.AreEqual(
            "suggestion",
            Assert.ThrowsExactly<ArgumentException>(() =>
                SuggestionReference.FromAdoptedSuggestion(
                    CreateSuggestion(
                        kind: SuggestionKind.TermCorrection,
                        status: SuggestionStatus.Adopted))).ParamName);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => SuggestionReference.FromAdoptedSuggestion(null!));
    }

    private static ModelRun CreateModelRun(
        string provider = "local-rules",
        string modelIdentifier = "silence-detector",
        string modelVersion = "1.0.0",
        ModelHashAvailability modelHashAvailability = ModelHashAvailability.Available,
        ModelRunOutcome outcome = ModelRunOutcome.Succeeded,
        string? errorCode = null,
        ModelExecutionLocation executionLocation = ModelExecutionLocation.Local,
        string? destination = null,
        string? region = null,
        string? sentDataClassification = null,
        string? retentionPolicy = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null)
    {
        DateTimeOffset start = startedAt
            ?? new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        return NewModelRun(
            provider,
            modelIdentifier,
            modelVersion,
            modelHashAvailability == ModelHashAvailability.Available ? Digest('a') : null,
            modelHashAvailability,
            Digest('d'),
            Digest('b'),
            outcome == ModelRunOutcome.Succeeded ? Digest('c') : null,
            outcome,
            errorCode,
            executionLocation,
            destination,
            region,
            sentDataClassification,
            retentionPolicy,
            start,
            completedAt ?? start);
    }

    private static ModelRun NewModelRun(
        string provider = "local-rules",
        string modelIdentifier = "silence-detector",
        string modelVersion = "1.0.0",
        ContentDigest? modelHash = null,
        ModelHashAvailability modelHashAvailability = ModelHashAvailability.Available,
        ContentDigest? configurationDigest = null,
        ContentDigest? inputDigest = null,
        ContentDigest? outputDigest = null,
        ModelRunOutcome outcome = ModelRunOutcome.Succeeded,
        string? errorCode = null,
        ModelExecutionLocation executionLocation = ModelExecutionLocation.Local,
        string? destination = null,
        string? region = null,
        string? sentDataClassification = null,
        string? retentionPolicy = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null)
    {
        DateTimeOffset start = startedAt
            ?? new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        return new ModelRun(
            EntityId<ModelRunIdTag>.New(),
            provider,
            modelIdentifier,
            modelVersion,
            modelHash,
            modelHashAvailability,
            configurationDigest ?? Digest('d'),
            inputDigest ?? Digest('b'),
            outputDigest,
            outcome,
            errorCode,
            executionLocation,
            destination,
            region,
            sentDataClassification,
            retentionPolicy,
            start,
            completedAt ?? start);
    }

    private static Suggestion CreateSuggestion(
        SuggestionKind kind = SuggestionKind.Highlight,
        SuggestionStatus status = SuggestionStatus.Proposed,
        OriginRef? origin = null,
        string reason = "Candidate is independently understandable.",
        double? confidence = null,
        string? missingContext = null,
        string? warning = null)
    {
        return new Suggestion(
            EntityId<SuggestionIdTag>.New(),
            EntityId<ModelRunIdTag>.New(),
            kind,
            status,
            origin ?? OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New()),
            reason,
            confidence,
            missingContext,
            warning);
    }

    private static ContentDigest Digest(char value) => new(new string(value, 64));

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        Assert.IsTrue(type.IsSealed);
        System.Reflection.PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }
}