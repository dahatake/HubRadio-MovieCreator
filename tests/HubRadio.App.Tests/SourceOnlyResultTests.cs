using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Workflow;
using HubRadio.Core.Rendering;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlyResultTests
{
    [TestMethod]
    public async Task CanonicalRoundTripAndRestartValidateActualFilesAndImmutableEvaluationRows()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        SourceOnlyResult expected = await f.EvaluateAsync();
        SourceOnlyResult parsed = SourceOnlyResult.ParseCanonical(expected.CanonicalJson, expected.CanonicalDigest);
        Assert.AreEqual(expected.CanonicalJson, parsed.CanonicalJson);
        Assert.AreEqual(expected.CanonicalDigest, parsed.CanonicalDigest);
        Assert.AreEqual(expected.Binding, parsed.Binding);
        Assert.HasCount(6, parsed.Artifacts);
        Assert.HasCount(1, parsed.TranscriptReferences);
        var restarted = new SourceOnlyResultService(new ArtifactStore(f.Generation.DatabasePath, f.Generation.Root),
            new AutomatedEvaluationStore(f.Generation.DatabasePath), new QuickStartWorkflowStore(f.Generation.DatabasePath),
            new ProjectStore(f.Generation.DatabasePath));
        StoredSourceOnlyExecution persisted = f.PersistResult(parsed);
        StoredSourceOnlyExecution restoredExecution = new SourceOnlyExecutionStore(f.Generation.DatabasePath).Get(persisted.Request.ExecutionId)!;
        Assert.AreEqual(persisted.StateDigest, restoredExecution.StateDigest);
        Assert.AreEqual(expected.CanonicalJson, restoredExecution.ResultJson);
        Assert.AreEqual(expected.CanonicalDigest, restoredExecution.ResultDigest);
        Assert.AreEqual(expected.Binding.VersionId, restoredExecution.VersionId);
        Assert.AreEqual(f.Generation.Parent.Id, f.Projects.GetCompositionVersion(restoredExecution.VersionId!)!.ParentVersionId);
        SourceOnlyResultLoadResult loaded = await restarted.LoadAsync(restoredExecution);
        Assert.IsTrue(loaded.Validation.IsValid, string.Join(",", loaded.Validation.Failures.Select(x => x.Code)));
        Assert.IsTrue(loaded.Result.RequiresSetDeliveryValidation);
        Assert.IsTrue(loaded.Result.Artifacts.All(a => !a.Delivered));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SourceOnlyResultArtifact>)parsed.Artifacts).Clear());
        string projection = JsonSerializer.Serialize(parsed.ToWebDto());
        Assert.IsFalse(projection.Contains("StorageReference", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projection.Contains("stagingPath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projection.Contains(f.Generation.Root, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projection.Contains("file:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AncillaryArtifactsRoundTripAndAbsentOnesKeepTheExactLegacyBytes()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        SourceOnlyResult legacy = await f.EvaluateAsync();

        Assert.IsEmpty(legacy.AncillaryArtifacts);
        Assert.IsFalse(legacy.CanonicalJson.Contains("ancillaryArtifacts", StringComparison.Ordinal),
            "A result without ancillary artifacts must keep its pre-existing canonical bytes.");
        SourceOnlyResult reparsed = SourceOnlyResult.ParseCanonical(legacy.CanonicalJson, legacy.CanonicalDigest);
        Assert.AreEqual(legacy.CanonicalJson, reparsed.CanonicalJson);
        Assert.AreEqual(legacy.CanonicalDigest, reparsed.CanonicalDigest);
        Assert.IsEmpty(reparsed.AncillaryArtifacts);

        SourceOnlyResultEvidenceReference[] ancillary =
        [
            new(SourceOnlyResultEvidenceKind.Transcript, new SourceOnlyResultArtifactReference(
                EntityId<ArtifactIdTag>.New(), ContentDigest.Compute("transcript"u8.ToArray()), 42, ArtifactKind.Delivery)),
            new(SourceOnlyResultEvidenceKind.UsedSourceRanges, new SourceOnlyResultArtifactReference(
                EntityId<ArtifactIdTag>.New(), ContentDigest.Compute("ranges"u8.ToArray()), 24, ArtifactKind.Delivery)),
        ];
        SourceOnlyResult extended = SourceOnlyPipeline.BindAncillaryResult(legacy, ancillary, legacy.Omissions);

        Assert.HasCount(2, extended.AncillaryArtifacts);
        Assert.AreNotEqual(legacy.CanonicalDigest, extended.CanonicalDigest);
        SourceOnlyResult extendedParsed = SourceOnlyResult.ParseCanonical(
            extended.CanonicalJson, extended.CanonicalDigest);
        Assert.AreEqual(extended.CanonicalJson, extendedParsed.CanonicalJson);
        CollectionAssert.AreEqual(ancillary, extendedParsed.AncillaryArtifacts.ToArray());
        Assert.IsFalse(JsonSerializer.Serialize(extendedParsed.ToWebDto())
            .Contains(f.Generation.Root, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AncillaryArtifactOmissionKeepsEveryRenderedOutputInTheResult()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        SourceOnlyResult evaluated = await f.EvaluateAsync();

        SourceOnlyResult omitted = SourceOnlyPipeline.BindAncillaryResult(evaluated, [],
            [.. evaluated.Omissions, new SourceOnlyResultOmission(SourceOnlyResultOmissionRole.Other,
                SourceOnlyResultOmissionReason.UnrecognizedOmission)]);

        Assert.HasCount(evaluated.Artifacts.Count, omitted.Artifacts);
        Assert.IsEmpty(omitted.AncillaryArtifacts);
        Assert.IsFalse(omitted.HasBlockingFailures);
        Assert.IsTrue(omitted.HasWarnings);
        Assert.IsTrue(omitted.Omissions.Any(item =>
            item.Role == SourceOnlyResultOmissionRole.Other &&
            item.Reason == SourceOnlyResultOmissionReason.UnrecognizedOmission));
        Assert.AreEqual(omitted.CanonicalJson,
            SourceOnlyResult.ParseCanonical(omitted.CanonicalJson, omitted.CanonicalDigest).CanonicalJson);
    }

    [TestMethod]
    public async Task DeliveryRoundTripKeepsTheAncillaryArtifactsInsteadOfDroppingThem()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        SourceOnlyResultEvidenceReference[] ancillary =
        [
            new(SourceOnlyResultEvidenceKind.Transcript, new SourceOnlyResultArtifactReference(
                EntityId<ArtifactIdTag>.New(), ContentDigest.Compute("transcript"u8.ToArray()), 42, ArtifactKind.Delivery)),
            new(SourceOnlyResultEvidenceKind.UsedSourceRanges, new SourceOnlyResultArtifactReference(
                EntityId<ArtifactIdTag>.New(), ContentDigest.Compute("ranges"u8.ToArray()), 24, ArtifactKind.Delivery)),
        ];
        SourceOnlyResult basis = SourceOnlyPipeline.BindAncillaryResult(evaluated, ancillary, evaluated.Omissions);

        // WithoutDelivery must be a no-op for an undelivered result.
        Assert.AreEqual(basis.CanonicalJson, SourceOnlyDeliveryReceipt.WithoutDelivery(basis).CanonicalJson);

        // Reconciliation keeps the references so a manual-resolution result still
        // names the artifacts that were already promoted.
        SourceOnlyResult reconciliation = SourceOnlyDeliveryReceipt.Reconciliation(basis, []);
        CollectionAssert.AreEqual(ancillary, reconciliation.AncillaryArtifacts.ToArray());
    }

    [TestMethod]
    public async Task ChangedRealOutputRemainsVisibleButFailsRevalidation()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        SourceOnlyResultArtifact victim = result.Artifacts[0];
        string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(victim.ArtifactId);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        SourceOnlyResultLoadResult loaded = await f.Service.LoadAsync(f.PersistResult(result));
        Assert.IsFalse(loaded.Validation.IsValid);
        Assert.IsTrue(loaded.Validation.Failures.Any(x => x.ArtifactId == victim.ArtifactId && x.Code == SourceOnlyResultFailureCode.ArtifactValidationFailed));
        Assert.HasCount(result.Artifacts.Count, loaded.Result.Artifacts);
    }

    [TestMethod]
    public async Task ChangedQcEvidenceCannotBeUsedAfterRestart()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        SourceOnlyResultArtifact victim = result.Artifacts[0];
        SourceOnlyResultEvidenceReference proof = victim.Evidence.Single(e => e.Kind == SourceOnlyResultEvidenceKind.QualityControl);
        string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(proof.Artifact.ArtifactId);
        await File.WriteAllTextAsync(path, "{}", new UTF8Encoding(false));
        SourceOnlyResultValidation validation = await f.Service.ValidateAsync(result, f.Execution);
        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Failures.Any(x => x.ArtifactId == victim.ArtifactId));
    }

    [TestMethod]
    public async Task StructurallyValidChangedMeasurementsAreRejectedByStoredEvidence()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        JsonNode document = JsonNode.Parse(result.CanonicalJson)!;
        document["artifacts"]![0]!["measurements"]!["loudness"]!["value"] = -30;
        // Parse is deliberately separate from physical/evaluation-store validation.
        SourceOnlyResult forged = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(document.ToJsonString()));
        Assert.IsFalse((await f.Service.ValidateAsync(forged, f.Execution)).IsValid);
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(forged.CanonicalJson, result.CanonicalDigest));
    }

    [TestMethod]
    public async Task ForeignEvaluationOrEvidenceCannotBeSubstitutedForAnotherArtifact()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        JsonNode document = JsonNode.Parse(result.CanonicalJson)!;
        document["artifacts"]![1]!["evaluation"] = document["artifacts"]![0]!["evaluation"]!.DeepClone();
        document["artifacts"]![1]!["evaluationDigest"] = document["artifacts"]![0]!["evaluationDigest"]!.DeepClone();
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(document.ToJsonString())));
        document = JsonNode.Parse(result.CanonicalJson)!;
        // Graph proof substitution has a valid ID/digest/size, but belongs to a different rendition.
        document["artifacts"]![1]!["evidence"]![0] = document["artifacts"]![0]!["evidence"]![0]!.DeepClone();
        SourceOnlyResult wrongProof = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(document.ToJsonString()));
        Assert.IsFalse((await f.Service.ValidateAsync(wrongProof, f.Execution)).IsValid);
    }

    [TestMethod]
    public async Task CurrentProjectDriftIsStaleEvidenceNotFreshAuthorization()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredProject stored = f.Projects.GetProject(f.Generation.Project.Id)!;
        _ = f.Projects.UpdateProject(f.Generation.Project, stored.CanonicalJson, stored.Revision, SourceOnlyEvaluationFixture.Now.AddMinutes(1));
        SourceOnlyResultValidation validation = await f.Service.ValidateAsync(result, f.Execution);
        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Failures.Any(x => x.Code == SourceOnlyResultFailureCode.StoredEvidenceInvalid));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"binding\":{},\"binding\":{}}")]
    [DataRow("{\"binding\":{},\"unknown\":true}")]
    [DataRow("null")]
    [DataRow("[]")]
    public void RejectsMalformedClosedRoot(string json) => Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(json));

    [TestMethod]
    public void RejectsNoncanonicalUnknownDuplicateAndOversizedPayloads()
    {
        SourceOnlyResult valid = FailureOnly();
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(" " + valid.CanonicalJson));
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(valid.CanonicalJson + "\n"));
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(new string(' ', SourceOnlyResult.MaximumCanonicalJsonUtf8Bytes + 1)));
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(valid.CanonicalJson.Replace("\"binding\":", "\"binding\":{},\"binding\":", StringComparison.Ordinal)));
        JsonNode node = JsonNode.Parse(valid.CanonicalJson)!;
        node["binding"]!["path"] = @"C:\private\output.mp4";
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString())));
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("2")]
    [DataRow("completed")]
    [DataRow("Unknown")]
    public void RejectsNumericOrWrongCaseEnums(string value)
    {
        JsonNode node = JsonNode.Parse(FailureOnly().CanonicalJson)!;
        node["generationDisposition"] = value;
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString())));
    }

    [TestMethod]
    [DataRow("9007199254740992")]
    [DataRow("1.0000000000000000000000000000001")]
    [DataRow("1e-1000")]
    [DataRow("1e1000")]
    [DataRow("-1")]
    [DataRow("0")]
    [DataRow("\"10\"")]
    public void RejectsUnsafeOrNonintegralSizesExactly(string number)
    {
        JsonNode node = JsonNode.Parse(FailureOnly(withReconciliation: true).CanonicalJson)!;
        node["reconciliationArtifacts"]![0]!["byteSize"] = JsonNode.Parse(number);
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString())));
    }

    [TestMethod]
    [DataRow("10", 10L)]
    [DataRow("1e1", 10L)]
    [DataRow("9007199254740991", SourceOnlyResult.MaximumSafeInteger)]
    public void AcceptsCanonicalMathematicalSafeIntegers(string number, long expected)
    {
        JsonNode node = JsonNode.Parse(FailureOnly(withReconciliation: true).CanonicalJson)!;
        node["reconciliationArtifacts"]![0]!["byteSize"] = JsonNode.Parse(number);
        SourceOnlyResult parsed = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString()));
        Assert.AreEqual(expected, parsed.ReconciliationArtifacts[0].ByteSize);
    }

    [TestMethod]
    public void EnforcesTopLevelCountBoundsBeforeMaterializingCollections()
    {
        foreach ((string key, int max, JsonNode item) in new[]
        {
            ("artifacts", SourceOnlyResult.MaximumArtifactCount, (JsonNode)new JsonObject()),
            ("omissions", SourceOnlyResult.MaximumOmissionCount, (JsonNode)new JsonObject { ["role"] = "Other", ["reason"] = "NoDisplayCues" }),
            ("failures", SourceOnlyResult.MaximumFailureCount, (JsonNode)new JsonObject { ["code"] = "GenerationFailed", ["artifactId"] = null }),
        })
        {
            JsonNode node = JsonNode.Parse(FailureOnly().CanonicalJson)!;
            var array = new JsonArray();
            for (int i = 0; i <= max; i++) array.Add(item.DeepClone());
            node[key] = array;
            Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString())));
        }
    }

    [TestMethod]
    public async Task ResultBindingAndDeliveryBooleanCannotForgeAuthority()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        JsonNode node = JsonNode.Parse(result.CanonicalJson)!;
        node["binding"]!["executionId"] = EntityId<SourceOnlyExecutionIdTag>.New().ToString();
        SourceOnlyResult foreign = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString()));
        Assert.ThrowsExactly<SourceOnlyResultException>(() => foreign.ValidateBinding(f.Execution));
        node = JsonNode.Parse(result.CanonicalJson)!;
        node["artifacts"]![0]!["delivered"] = true;
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString())));
    }

    [TestMethod]
    public async Task WarningProjectionIsStableBoundAndCannotBeEditedIndependentlyOfEvaluation()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        SourceOnlyResultArtifact artifact = result.Artifacts[1];
        SourceOnlyResultWarning warning = artifact.Warnings.First();
        SourceOnlyResultWarningDto projected = result.ToWebDto().Artifacts[1].Warnings.First();
        Assert.AreEqual(warning.Id, projected.Id);
        Assert.AreEqual(artifact.ArtifactId.ToString(), projected.Target.ArtifactId);
        Assert.AreEqual(artifact.ArtifactDigest.Hex, projected.Target.ArtifactDigest);
        Assert.AreEqual(result.Binding.ProjectId.ToString(), projected.Target.ProjectId);
        Assert.AreEqual(artifact.VersionId.ToString(), projected.Target.VersionId);
        Assert.AreEqual(artifact.VersionDigest.Hex, projected.Target.VersionDigest);
        JsonNode node = JsonNode.Parse(result.CanonicalJson)!;
        node["artifacts"]![1]!["warnings"]![0]!["target"]!["artifactId"] = EntityId<ArtifactIdTag>.New().ToString();
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(node.ToJsonString())));
    }

    [TestMethod]
    public async Task SelfReviewPreservesLoadableHistoricalPrefixWithoutFreezingCurrentDeliveryRevision()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution persisted = f.PersistResult(result);
        var binding = new QuickStartWorkflowBinding(result.Binding.ProjectId, result.VersionId, result.VersionDigest);
        QuickStartWorkflow original = f.Workflows.Get(binding)!;
        Assert.AreEqual(result.PrimaryWorkflow!.Revision, original.Revision);
        QuickStartWorkflow current = f.Workflows.Append(QuickStartWorkflowEvent.SelfReviewed(EntityId<QuickStartWorkflowEventIdTag>.New(),
            binding, QuickStartActor.User(f.Execution.Request.ActorSid), SourceOnlyEvaluationFixture.Now.AddMinutes(1)), original.Revision);
        Assert.AreEqual(original.Revision + 1, current.Revision);
        Assert.AreNotEqual(original.CanonicalDigest, current.CanonicalDigest);
        SourceOnlyResultLoadResult loaded = await f.Service.LoadAsync(new SourceOnlyExecutionStore(f.Generation.DatabasePath).Get(persisted.Request.ExecutionId)!);
        Assert.IsTrue(loaded.Validation.IsValid, string.Join(",", loaded.Validation.Failures.Select(x => x.Code)));
        Assert.AreEqual(original.Revision, loaded.Result.PrimaryWorkflow!.Revision);
        Assert.AreEqual(current.Revision, new QuickStartWorkflowStore(f.Generation.DatabasePath).Get(binding)!.Revision);
        Assert.IsFalse(loaded.Result.Artifacts.Any(a => a.Delivered));
        SourceOnlyResult reused = await new SourceOnlyOutputEvaluation(f.Generation.Store,
            (_, _) => throw new AssertFailedException("Historical replay must not run QC."), f.Evaluations, f.Workflows, f.Projects)
            .EvaluateAsync(persisted, f.Generated, f.Generation.SettingsProfile, f.Generation.Settings);
        Assert.AreEqual(result.CanonicalJson, reused.CanonicalJson);
    }

    [TestMethod]
    public async Task ForgedWorkflowPrefixDigestIsRejectedDespiteMatchingCurrentEvaluation()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        JsonNode json = JsonNode.Parse(result.CanonicalJson)!;
        json["primaryWorkflow"]!["canonicalDigest"] = new string('e', 64);
        SourceOnlyResult forged = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(json.ToJsonString()));
        SourceOnlyResultValidation validation = await f.Service.ValidateAsync(forged, f.Execution);
        Assert.IsTrue(validation.Failures.Any(x => x.Code == SourceOnlyResultFailureCode.WorkflowConflict));
    }

    [TestMethod]
    public async Task ActualAssBytesProfilesAndSourceCueMappingSurviveStagingCleanupAndRestart()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        SourceOnlyResultArtifact artifact = result.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeBurnIn);
        StandardGeneratedArtifact generated = f.Generated.Artifacts.Single(a => a.Artifact.Id == artifact.ArtifactId);
        var store = new SourceOnlyResultEvidenceStore(f.Generation.Store);
        SourceOnlyNativeEvidence document = await store.ReadAsync(result.Binding, artifact,
            artifact.Evidence.Single(e => e.Kind == SourceOnlyResultEvidenceKind.RenderingInputs), CancellationToken.None);
        SourceOnlyRenderingInputsPayload proof = SourceOnlyResultInputValidation.Read(document.Payload);
        Assert.AreEqual(generated.BindingEvidence.DisplayCueDigest, SourceOnlyResultEvidenceStore.PayloadDigest(proof.DisplayCues));
        byte[] actual = proof.SubtitleFiles.Single().Base64Chunks.SelectMany(Convert.FromBase64String).ToArray();
        CollectionAssert.AreEqual(generated.SubtitleFiles.Single().File.GetUtf8Bytes(), actual);
        Assert.AreEqual(generated.SubtitleFiles.Single().Input.ContentDigest, ContentDigest.Compute(actual));
        Assert.AreEqual(generated.SubtitleEvidence!.DisplayProfileDigest.Hex,
            SourceOnlyResultEvidenceStore.NativeDigest(proof.SubtitleEvidence!.Value, "DisplayProfileDigest"));
        Assert.IsNotNull(proof.SourceCueMappingDigest);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(f.Generation.Root, "projects", f.Generation.Project.Id.ToString(), "staging"),
            "*.ass", SearchOption.AllDirectories));
        Assert.IsTrue((await f.Service.LoadAsync(f.PersistResult(result))).Validation.IsValid);
    }

    [TestMethod]
    public async Task StoredRawQcCannotRewriteIndependentDisplayStatisticsOrFrameCount()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        SourceOnlyResultArtifact artifact = result.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeBurnIn);
        var store = new SourceOnlyResultEvidenceStore(f.Generation.Store);
        async Task<JsonElement> Payload(SourceOnlyResultEvidenceKind kind) => (await store.ReadAsync(result.Binding, artifact,
            artifact.Evidence.Single(e => e.Kind == kind), CancellationToken.None)).Payload;
        SourceOnlyRenderingInputsPayload inputs = SourceOnlyResultInputValidation.Read(await Payload(SourceOnlyResultEvidenceKind.RenderingInputs));
        JsonElement graph = await Payload(SourceOnlyResultEvidenceKind.Graph), manifest = await Payload(SourceOnlyResultEvidenceKind.Manifest);
        IReadOnlyList<SourceOnlyResultSubtitleMeasurement> cues = SourceOnlyResultInputValidation.Validate(inputs, artifact,
            f.Generation.Preparation, f.Generation.Settings, graph, manifest);
        SourceOnlyQcEvidencePayload qc = JsonSerializer.Deserialize<SourceOnlyQcEvidencePayload>(await Payload(SourceOnlyResultEvidenceKind.QualityControl),
            SourceOnlyResultCodec.Options)!;
        SourceOnlyResultRawQc.Validate(qc.OutputQc!.Value, artifact, graph, manifest, inputs, qc.QcTools!.Value, cues);
        SourceOnlyResultMeasurementsDto measurements = SourceOnlyResultCodec.MeasurementsDto(artifact.Measurements)!;
        SourceOnlyResultCueDto changed = measurements.Cues[0] with { CharacterCount = 1, MaximumLineCharacters = 1 };
        SourceOnlyResultMeasurements? forgedMeasurements = SourceOnlyResultCodec.Measurements(measurements with { Cues = [changed] });
        var forged = new SourceOnlyResultArtifact(artifact.Role, artifact.RenditionId, artifact.Artifact, artifact.VersionId,
            artifact.VersionDigest, artifact.Source, artifact.GraphDigest, artifact.ManifestDigest, artifact.Evidence, artifact.Evaluation,
            forgedMeasurements, artifact.FailureCode, profileId: artifact.ProfileId, profileDigest: artifact.ProfileDigest);
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResultRawQc.Validate(qc.OutputQc.Value, forged, graph, manifest, inputs, qc.QcTools.Value, cues));
        JsonNode raw = JsonNode.Parse(qc.OutputQc.Value.GetRawText())!;
        raw["VideoTiming"]!["ObservedFrameCount"] = 1;
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResultRawQc.Validate(SourceOnlyResultCodec.Element(raw.ToJsonString()),
            artifact, graph, manifest, inputs, qc.QcTools.Value, cues));
    }

    [TestMethod]
    public async Task IndependentGeneratorCheckpointRejectsRehashedManifestAndKeepsWarnings()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        await f.SaveGeneratorCheckpointAsync();
        StandardGeneratedArtifact first = f.Generated.Artifacts[0];
        RenderManifest m = first.Manifest;
        var manifest = new RenderManifest(m.Id, first.Context.CompositionVersion, first.Context.TimelineMap, m.TimelineMapDigest,
            first.Context.Profile, m.ConfigurationDigest, "unrecorded-version", m.ApplicationDigest, m.Inputs, m.Tools, m.Models, m.Fonts, m.Outputs);
        var changed = first with
        {
            Manifest = manifest,
            QualityControlRequest = new HubRadio.Infrastructure.Rendering.OutputQcRequest(
            first.Artifact.Id, manifest, first.Graph, first.Context, first.SubtitleEvidence, TimeSpan.FromMinutes(1))
        };
        var generated = new StandardArtifactGenerationResult(f.Generated.Disposition, [changed, .. f.Generated.Artifacts.Skip(1)],
            f.Generated.Omissions, [], null, null, false, f.Generated.Warnings);
        SourceOnlyResult result = await f.EvaluateAsync(generated: generated);
        Assert.AreEqual(SourceOnlyResultFailureCode.BindingMismatch, result.Artifacts[0].FailureCode);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, result.Artifacts[0].Evaluation!.Rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.Provenance).Status);
        Assert.IsTrue(result.GenerationWarnings.Select(w => (w.Role, w.Code)).SequenceEqual(f.Generated.Warnings.Select(w => (w.Role, w.Code))));
    }

    [TestMethod]
    public async Task UnchangedCheckpointAndGenerationWarningsRoundTripAndCannotBeIndependentlyRemoved()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true,
            includeThumbnail: true, useSubtitleBuilder: true);
        await f.SaveGeneratorCheckpointAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        Assert.IsTrue((await f.Service.ValidateAsync(result, f.Execution)).IsValid);
        Assert.IsFalse(result.HasBlockingFailures);
        Assert.IsTrue(result.HasWarnings);
        CollectionAssert.AreEqual(result.GenerationWarnings.ToArray(), SourceOnlyResult.ParseCanonical(result.CanonicalJson).GenerationWarnings.ToArray());
        JsonNode json = JsonNode.Parse(result.CanonicalJson)!;
        json["generationWarnings"] = new JsonArray();
        SourceOnlyResult forged = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(json.ToJsonString()));
        Assert.IsFalse((await f.Service.ValidateAsync(forged, f.Execution)).IsValid);

        int index = result.GenerationWarnings.ToList().FindIndex(w => w.Role == "landscape-subtitles" && w.Code == "NOT EVALUATED");
        Assert.IsTrue(index >= 0);
        foreach ((string key, string value) in new[] { ("code", "no-real-asr-cues"), ("role", "human-evaluation") })
        {
            json = JsonNode.Parse(result.CanonicalJson)!;
            json["generationWarnings"]![index]![key] = value;
            // Both values are individually permitted, but cannot replace the generator's exact facts.
            forged = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(json.ToJsonString()));
            Assert.IsFalse((await f.Service.ValidateAsync(forged, f.Execution)).IsValid);
        }
    }

    [TestMethod]
    public void ActualGenerationWarningVectorSurvivesFailureResultCanonicalAndWebProjection()
    {
        SourceOnlyResultGenerationWarning[] warnings =
        [
            new("landscape-subtitles", "opaque-setting-explicitly-overridden-by-transparent-background-request"),
            new("landscape-subtitles", "NOT EVALUATED"),
            new("human-evaluation", "NOT EVALUATED"),
        ];
        SourceOnlyResult result = FailureOnly(generationWarnings: warnings);
        SourceOnlyResult parsed = SourceOnlyResult.ParseCanonical(result.CanonicalJson, result.CanonicalDigest);
        CollectionAssert.AreEqual(warnings, parsed.GenerationWarnings.ToArray());
        CollectionAssert.AreEqual(warnings.Select(w => new SourceOnlyResultGenerationWarningDto(w.Role, w.Code)).ToArray(),
            parsed.ToWebDto().GenerationWarnings);
        Assert.AreEqual(result.CanonicalJson, parsed.CanonicalJson);
        Assert.IsTrue(parsed.HasWarnings);
        Assert.IsTrue(parsed.HasBlockingFailures);
        Assert.IsNull(parsed.PrimaryWorkflow);
        Assert.IsEmpty(parsed.Artifacts); // Accepting a warning does not fabricate QC or successful output.
    }

    [TestMethod]
    [DataRow("plan")]
    [DataRow("music")]
    [DataRow("video")]
    [DataRow("landscape-subtitles")]
    [DataRow("human-evaluation")]
    [DataRow("landscape-clean")]
    [DataRow("landscape-burn-in")]
    [DataRow("landscape-srt")]
    [DataRow("landscape-vtt")]
    [DataRow("vertical-subtitles-aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [DataRow("vertical-burn-in-aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [DataRow("thumbnail-landscape-aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [DataRow("thumbnail-vertical-aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    public void GenerationWarningRolesAreClosedSubjectsNotArtifactAuthorization(string role)
    {
        SourceOnlyResult result = FailureOnly(generationWarnings: [new(role, "NOT EVALUATED")]);
        Assert.AreEqual(role, SourceOnlyResult.ParseCanonical(result.CanonicalJson).GenerationWarnings.Single().Role);
        if (role is "landscape-subtitles" or "human-evaluation" || role.StartsWith("vertical-subtitles-", StringComparison.Ordinal))
            Assert.ThrowsExactly<JsonException>(() => SourceOnlyOutputEvaluation.ParseRole(role));
    }

    [TestMethod]
    public void ClosedGenerationWarningVocabularyCoversProducerEnumsAndRoundTripsEveryKnownReason()
    {
        string[] enumCodes = Enum.GetNames<StandardCompositionWarningCode>().Concat(Enum.GetNames<MusicCatalogWarning>())
            .Concat(Enum.GetValues<MusicAssignmentReason>().Where(r => r != MusicAssignmentReason.Assigned).Select(r => r.ToString())).ToArray();
        foreach (string code in enumCodes) CollectionAssert.Contains(SourceOnlyResultCodec.GenerationWarningCodes.ToArray(), code);
        foreach (string code in SourceOnlyResultCodec.GenerationWarningCodes)
        {
            SourceOnlyResult result = FailureOnly(generationWarnings: [new("landscape-subtitles", code)]);
            Assert.AreEqual(code, SourceOnlyResult.ParseCanonical(result.CanonicalJson).GenerationWarnings.Single().Code);
        }
        Assert.AreEqual(SourceOnlyResultCodec.GenerationWarningCodes.Count, SourceOnlyResultCodec.GenerationWarningCodes.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    [DataRow(null, "NOT EVALUATED")]
    [DataRow("", "NOT EVALUATED")]
    [DataRow("manual", "NOT EVALUATED")]
    [DataRow("landscape-subtitles/private", "NOT EVALUATED")]
    [DataRow("human-evaluation-private", "NOT EVALUATED")]
    [DataRow("vertical-subtitles-", "NOT EVALUATED")]
    [DataRow("vertical-subtitles-../private", "NOT EVALUATED")]
    [DataRow("vertical-subtitles-AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA", "NOT EVALUATED")]
    [DataRow("thumbnail-landscape-aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/private", "NOT EVALUATED")]
    [DataRow("landscape-subtitles\n", "NOT EVALUATED")]
    [DataRow("landscape-subtitles", null)]
    [DataRow("landscape-subtitles", "")]
    [DataRow("landscape-subtitles", "UnknownManualCode")]
    [DataRow("landscape-subtitles", "private source text")]
    [DataRow("landscape-subtitles", "C:\\private\\source.mov")]
    [DataRow("landscape-subtitles", "/private/source.mov")]
    [DataRow("landscape-subtitles", "file:///private/source.mov")]
    [DataRow("landscape-subtitles", "missing-token-timing/private")]
    [DataRow("landscape-subtitles", "NOT EVALUATED\n")]
    [DataRow("landscape-subtitles", "NOT\tEVALUATED")]
    [DataRow("landscape-subtitles", "NOT\0EVALUATED")]
    [DataRow("landscape-subtitles", "NOT\u00a0EVALUATED")]
    [DataRow("landscape-subtitles", "NOT  EVALUATED")]
    [DataRow("landscape-subtitles", " NOT EVALUATED")]
    [DataRow("landscape-subtitles", "NOT EVALUATED ")]
    [DataRow("landscape-subtitles", "not evaluated")]
    [DataRow("landscape-subtitles", "NOT_EVALUATED")]
    [DataRow("human-evaluation", "PASS")]
    [DataRow("music", "Assigned")]
    public void GenerationWarningsRejectUnknownPrivateOrMalformedValuesWithoutEcho(string? role, string? code)
    {
        Assert.ThrowsExactly<JsonException>(() => FailureOnly(generationWarnings: [new(role!, code!)]));
        JsonNode json = JsonNode.Parse(FailureOnly().CanonicalJson)!;
        json["generationWarnings"] = new JsonArray(new JsonObject { ["role"] = role, ["code"] = code });
        JsonException error = Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(json.ToJsonString())));
        Assert.AreEqual("Invalid source-only canonical evidence.", error.Message);
    }

    [TestMethod]
    public void GenerationWarningsKeepStringAndCountBounds()
    {
        SourceOnlyResultGenerationWarning warning = new("landscape-subtitles", "NOT EVALUATED");
        SourceOnlyResult result = FailureOnly(generationWarnings: Enumerable.Repeat(warning, SourceOnlyResult.MaximumGenerationWarningCount).ToArray());
        Assert.HasCount(SourceOnlyResult.MaximumGenerationWarningCount, SourceOnlyResult.ParseCanonical(result.CanonicalJson).GenerationWarnings);
        foreach (SourceOnlyResultGenerationWarning[] invalid in new[]
        {
            new[] { warning with { Role = new string('a', 129) } },
            new[] { warning with { Code = new string('a', 128) } }, // Length alone does not authorize private ASCII text.
            new[] { warning with { Code = new string('a', 129) } },
            Enumerable.Repeat(warning, SourceOnlyResult.MaximumGenerationWarningCount + 1).ToArray(),
        })
        {
            Assert.ThrowsExactly<JsonException>(() => FailureOnly(generationWarnings: invalid));
            JsonNode json = JsonNode.Parse(FailureOnly().CanonicalJson)!;
            json["generationWarnings"] = new JsonArray(invalid.Select(w => (JsonNode)new JsonObject { ["role"] = w.Role, ["code"] = w.Code }).ToArray());
            Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(json.ToJsonString())));
        }
    }

    [TestMethod]
    public async Task EvidenceMoveThenRollbackFailurePersistsAttemptAndNativePathsWithoutWebLeak()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        ArtifactPromotionRequest? failedAttempt = null;
        int calls = 0;
        SourceOnlyResult result = await f.EvaluateAsync(promoteEvidence: async (request, ct) =>
        {
            if (++calls != 4) return await f.Generation.Store.PromoteAsync(request, ct);
            failedAttempt = request;
            string destination = Path.Combine(f.Generation.Root, "projects", request.ProjectId.ToString(), "evidence",
                request.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(request.StagingPath, destination);
            throw new ArtifactStoreException(ArtifactStoreFailureReason.RollbackFailed, reconciliationRequired: true);
        });
        Assert.IsNotNull(failedAttempt);
        Assert.IsTrue(result.ReconciliationRequired);
        Assert.AreEqual(StandardArtifactGenerationDisposition.ReconciliationRequired, result.GenerationDisposition);
        Assert.IsTrue(result.HasBlockingFailures);
        Assert.HasCount(f.Generated.Artifacts.Count, result.Artifacts);
        Assert.IsTrue(result.Artifacts.Skip(1).All(a => a.Evaluation is not null));
        SourceOnlyResultPromotionAttempt attempt = result.NativeReconciliationAttempts.Single();
        Assert.AreEqual(failedAttempt.Id, attempt.Artifact.ArtifactId);
        Assert.AreEqual(failedAttempt.ExpectedDigest, attempt.Artifact.ContentDigest);
        Assert.AreEqual(failedAttempt.ExpectedByteSize, attempt.Artifact.ByteSize);
        Assert.AreEqual(ArtifactKind.Evidence, attempt.Artifact.Kind);
        Assert.AreEqual(SourceOnlyResultEvidenceKind.QualityControl, attempt.EvidenceKind);
        Assert.AreEqual(failedAttempt.StagingPath, attempt.StagingPath);
        Assert.IsNull(f.Generation.Store.GetArtifact(attempt.Artifact.ArtifactId));
        string moved = Path.Combine(f.Generation.Root, attempt.StorageReference.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual(attempt.Artifact.ContentDigest, ContentDigest.Compute(await File.ReadAllBytesAsync(moved)));
        SourceOnlyResultFailure failure = result.Failures.Single(x => x.Code == SourceOnlyResultFailureCode.EvidencePromotionReconciliationRequired);
        Assert.AreEqual(attempt.Artifact, failure.PromotionArtifact);
        Assert.AreEqual(attempt.EvidenceKind, failure.EvidenceKind);
        Assert.AreEqual(result.Artifacts[0].ArtifactId, failure.ArtifactId);
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, stored.Phase);
        SourceOnlyResultLoadResult loaded = await f.Service.LoadAsync(stored);
        Assert.AreEqual(attempt, loaded.Result.NativeReconciliationAttempts.Single());
        Assert.IsFalse(loaded.Validation.IsValid); // An unregistered attempted move is not validated evidence.
        string web = JsonSerializer.Serialize(loaded.Result.ToWebDto());
        Assert.IsFalse(web.Contains("stagingPath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(web.Contains("storageReference", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(web.Contains(f.Generation.Root, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ThumbnailDtoCannotPretendToBeVideoOrBorrowAnotherProfile()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        SourceOnlyResult result = await f.EvaluateAsync();
        int index = result.Artifacts.ToList().FindIndex(a => a.IsImage);
        foreach ((string key, string value) in new[] { ("role", "LandscapeClean"), ("mediaType", "video/mp4"), ("qcApplicability", "VideoOutputQc") })
        {
            JsonNode json = JsonNode.Parse(result.CanonicalJson)!;
            json["artifacts"]![index]![key] = value;
            Assert.ThrowsExactly<JsonException>(() => SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(json.ToJsonString())));
        }
        JsonNode changed = JsonNode.Parse(result.CanonicalJson)!;
        changed["artifacts"]![index]!["profileId"] = result.Artifacts[0].ProfileId!.ToString();
        SourceOnlyResult foreign = SourceOnlyResult.ParseCanonical(CanonicalJson.Canonicalize(changed.ToJsonString()));
        Assert.IsFalse((await f.Service.ValidateAsync(foreign, f.Execution)).IsValid);
    }

    private static SourceOnlyResult FailureOnly(bool withReconciliation = false,
        IReadOnlyList<SourceOnlyResultGenerationWarning>? generationWarnings = null)
    {
        var binding = new SourceOnlyResultBinding(EntityId<ProjectIdTag>.New(), EntityId<SourceOnlyExecutionIdTag>.New(),
            EntityId<JobIdTag>.New(), StandardGenerationFixture.Hash("settings"), EntityId<CompositionVersionIdTag>.New(), StandardGenerationFixture.Hash("version"));
        return new(binding, StandardArtifactGenerationDisposition.Failed, [], [], [new(SourceOnlyResultFailureCode.GenerationFailed)],
            withReconciliation ? [new(EntityId<ArtifactIdTag>.New(), StandardGenerationFixture.Hash("unpromoted"), 10, ArtifactKind.Render)] : [], [], null, false,
            generationWarnings);
    }
}